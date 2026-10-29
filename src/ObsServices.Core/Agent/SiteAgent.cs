using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ObsServices.Core.Protocol;

namespace ObsServices.Core.Agent;

/// <summary>
/// The hardware side of the telescope_control protocol: a site agent over the WebSocket at
/// <c>/ws/agent/{site}</c>. Subclass, override <see cref="StateAsync"/> (what your devices read right
/// now, addressed to the entities the manifest lists), <see cref="RunTaskAsync"/> (carry out an
/// observation, reporting frames as you go) and the command hooks you support, then
/// <see cref="RunAsync"/>. The base class keeps the socket up, reconnects with backoff, replays
/// telemetry that could not be sent during an outage (the service de-duplicates on timestamp),
/// publishes state at a cadence, dispatches what the service sends down and acknowledges commands.
/// Mirrors <c>observatory.agent.Agent</c> in the service's Python package.
/// </summary>
public abstract class SiteAgent {
    private const int MaxFrameBytes = 8 * 1024 * 1024;

    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim sendLock = new(1, 1);
    private readonly Queue<Up> backlog = new();
    private readonly Dictionary<string, (TaskHandle Handle, Task Run)> tasks = new();
    private readonly object gate = new();
    private ClientWebSocket? socket;

    protected SiteAgent(string baseUrl, string siteId, string token) {
        BaseUrl = baseUrl.TrimEnd('/');
        SiteId = siteId;
        Token = token;
    }

    // ---- identity and tuning ------------------------------------------------------------------

    /// <summary>Reported in <c>hello</c>.</summary>
    public string Name { get; set; } = "observatory-dotnet";

    public string Version { get; set; } = "0.1.0";

    public List<string> Capabilities { get; } = new();

    /// <summary>How often <see cref="StateAsync"/> is published.</summary>
    public TimeSpan StateInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Longest pause between reconnection attempts (doubles from one second).</summary>
    public TimeSpan ReconnectMax { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Give up on a handshake that has not completed in this long and retry.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>WebSocket ping cadence; the service answers with pongs.</summary>
    public TimeSpan KeepAliveInterval { get; set; } = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A ping unanswered for this long closes the socket and triggers a reconnect, so a half-open
    /// connection (cable pulled, NAT entry gone) is noticed within about a minute instead of whenever
    /// TCP gives up.
    /// </summary>
    public TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Why the last connection attempt or session ended, for status displays. Null while healthy.</summary>
    public string? LastError { get; private set; }

    /// <summary>Connection attempts that failed since the last successful one.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>Telemetry messages kept for replay while the service is unreachable.</summary>
    public int BacklogLimit { get; set; } = 20_000;

    public string BaseUrl { get; }

    public string SiteId { get; }

    protected string Token { get; }

    // ---- the site as the service describes it --------------------------------------------------

    /// <summary>Set from <c>welcome</c> and every later <c>manifest</c> frame.</summary>
    public Manifest Manifest { get; private set; } = new();

    /// <summary>Service clock minus local clock, seconds, from <c>welcome</c>.</summary>
    public double ServerTimeOffset { get; private set; }

    /// <summary>Set while the socket is open and <c>hello</c> has been sent.</summary>
    public AsyncSignal Connected { get; } = new();

    /// <summary>Set once the site's manifest has arrived and <see cref="OnManifestAsync"/> has run for it.</summary>
    public AsyncSignal Ready { get; } = new();

    public bool IsConnected => Connected.IsSet;

    public IReadOnlyList<InstrumentInfo> Instruments => Manifest.Instruments;

    /// <summary>An entity by id or by assembly instance name.</summary>
    public Entity Entity(string key) => Manifest.Entity(key) ?? Manifest.Instance(key) ?? throw new KeyNotFoundException(key);

    /// <summary>By id, mount instance name or name; the first instrument when <paramref name="key"/> is null.</summary>
    public InstrumentInfo Instrument(string? key = null) {
        if (key == null) {
            return Manifest.Instruments.FirstOrDefault() ?? throw new KeyNotFoundException("this site has no instrument");
        }

        return Manifest.Instrument(key) ?? throw new KeyNotFoundException(key);
    }

    /// <summary>A stable id for a device this agent declares (see <see cref="Channels.DeviceEntityId"/>).</summary>
    public string DeviceId(string name) => Channels.DeviceEntityId(SiteId, name);

    public string SiteEntityId => Channels.SiteEntityId(SiteId);

    public string AgentEntityId => Channels.AgentEntityId(SiteId);

    /// <summary>Unix seconds now; the stamp every published message carries.</summary>
    public virtual double Now() => Timestamp.Now();

    // ---- things to implement -------------------------------------------------------------------

    /// <summary>
    /// Entities this agent adds (kind <c>device</c>, ids from <see cref="DeviceId"/>) or annotates (a
    /// known id with extra properties the library does not list). Sent in <c>hello</c>.
    /// </summary>
    protected virtual IEnumerable<Entity> Entities() => Array.Empty<Entity>();

    /// <summary>
    /// Current readings, addressed to entity ids with the property names of the manifest and
    /// <see cref="Channels"/>. Return null to publish nothing this round; the timestamp is filled in
    /// when zero. Called every <see cref="StateInterval"/> once the manifest has arrived.
    /// </summary>
    protected virtual Task<StateMsg?> StateAsync(CancellationToken cancellationToken) => Task.FromResult<StateMsg?>(null);

    /// <summary>The site's entities arrived or changed. <see cref="Manifest"/> is already set.</summary>
    protected virtual Task OnManifestAsync(Manifest manifest, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    /// Decide whether to take a task at all, before it is reported running. Return false with a
    /// reason to have it reported as <c>skipped</c> (the service hands it to another agent or leaves
    /// it to the operator). Default: accept everything.
    /// </summary>
    protected virtual bool AcceptTask(ObservationTask task, out string? reason) {
        reason = null;
        return true;
    }

    /// <summary>
    /// Carry out one observation. Return normally for <c>completed</c>; throw for <c>failed</c>;
    /// watch <see cref="TaskHandle.Cancelled"/> and return (or throw <see cref="OperationCanceledException"/>)
    /// when it fires, which is reported as <c>cancelled</c>.
    /// </summary>
    protected abstract Task RunTaskAsync(TaskHandle handle);

    /// <summary>
    /// Handle a command (<see cref="CommandKinds"/>); return a note for the ack or null. Throw
    /// <see cref="NotSupportedException"/> for kinds this agent does not do; any other exception is
    /// acked as a failure with its message.
    /// </summary>
    protected virtual Task<string?> OnCommandAsync(Command command, CancellationToken cancellationToken) => throw new NotSupportedException(command.Kind);

    /// <summary>The service's preferred order for the open tasks on an instrument.</summary>
    protected virtual Task OnPlanAsync(string? instrument, IReadOnlyList<string> order, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Diagnostics. Hosts forward these to their own log.</summary>
    protected virtual void Log(Level level, string message, Exception? exception = null) {
    }

    // ---- lifecycle ------------------------------------------------------------------------------

    /// <summary>Keep the connection up until <see cref="Stop"/> is called or the token fires.</summary>
    public async Task RunAsync(CancellationToken cancellationToken = default) {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopping.Token);
        var ct = linked.Token;
        var delay = TimeSpan.FromSeconds(1);
        var uri = new Uri(WebSocketUrl());
        // Sampling runs for the agent's whole life, not per session: during an outage the readings
        // go into the backlog and the site's history has no hole once the service is back.
        var publisher = PublishLoopAsync(ct);

        while (!ct.IsCancellationRequested) {
            using var ws = new ClientWebSocket();

            try {
                ws.Options.SetRequestHeader("Authorization", $"Bearer {Token}");
                ws.Options.KeepAliveInterval = KeepAliveInterval;
                ws.Options.KeepAliveTimeout = KeepAliveTimeout;
                ws.Options.CollectHttpResponseDetails = true;

                using (var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct)) {
                    handshake.CancelAfter(ConnectTimeout);

                    try {
                        await ws.ConnectAsync(uri, handshake.Token).ConfigureAwait(false);
                    } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                        throw new TimeoutException($"no handshake within {ConnectTimeout.TotalSeconds:0}s");
                    }
                }

                delay = TimeSpan.FromSeconds(1);
                ConsecutiveFailures = 0;
                LastError = null;
                await SessionAsync(ws, ct).ConfigureAwait(false);
                LastError = "connection closed by the service";
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                break;
            } catch (Exception e) {
                Connected.Reset();
                socket = null;

                if (ct.IsCancellationRequested) {
                    break;
                }

                ConsecutiveFailures++;
                LastError = DescribeFailure(ws, e);
                Log(Level.Warn, $"agent socket: {LastError}; reconnecting in {delay.TotalSeconds:0}s", e);

                try {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                } catch (OperationCanceledException) {
                    break;
                }

                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, ReconnectMax.TotalSeconds));
            }
        }

        Connected.Reset();
        socket = null;
        await CancelAllAsync().ConfigureAwait(false);

        try {
            await publisher.ConfigureAwait(false);
        } catch (OperationCanceledException) {
        }
    }

    /// <summary>Ask <see cref="RunAsync"/> to return. Running tasks see their <see cref="TaskHandle.Cancelled"/> fire.</summary>
    public void Stop() => stopping.Cancel();

    public bool IsStopping => stopping.IsCancellationRequested;

    /// <summary>A refused handshake names its HTTP status, so a bad token reads differently from a down server.</summary>
    private static string DescribeFailure(ClientWebSocket ws, Exception e) {
        var status = ws.HttpStatusCode;

        if (status != 0) {
            var hint = (int)status switch {
                401 => " (the agent token was refused: mint a new one for this site)",
                403 => " (forbidden)",
                404 => " (no such site)",
                _ => string.Empty,
            };
            return $"service answered {(int)status} {status}{hint}";
        }

        return e.Message;
    }

    private string WebSocketUrl() {
        var ws = BaseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "wss://" + BaseUrl[8..]
            : BaseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? "ws://" + BaseUrl[7..]
            : BaseUrl;
        return $"{ws}/ws/agent/{SiteId}";
    }

    private async Task SessionAsync(ClientWebSocket ws, CancellationToken ct) {
        socket = ws;
        var hello = new Hello { Agent = Name, Version = Version, Protocol = ProtocolJson.Version, Capabilities = Capabilities.ToList(), Entities = Entities().ToList() };
        await SendRawAsync(ws, hello, ct).ConfigureAwait(false);

        // replay what the outage swallowed; the service keys samples on timestamp
        while (true) {
            Up? queued;

            lock (gate) {
                if (!backlog.TryDequeue(out queued)) {
                    break;
                }
            }

            await SendRawAsync(ws, queued, ct).ConfigureAwait(false);
        }

        Connected.Set();

        try {
            var buffer = new ArraySegment<byte>(new byte[64 * 1024]);
            using var frame = new MemoryStream();

            while (!ct.IsCancellationRequested) {
                frame.SetLength(0);
                WebSocketReceiveResult result;

                do {
                    result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close) {
                        return;
                    }

                    frame.Write(buffer.Array!, buffer.Offset, result.Count);

                    if (frame.Length > MaxFrameBytes) {
                        throw new InvalidOperationException("frame larger than 8 MiB");
                    }
                } while (!result.EndOfMessage);

                if (result.MessageType != WebSocketMessageType.Text) {
                    continue;
                }

                var text = Encoding.UTF8.GetString(frame.GetBuffer(), 0, (int)frame.Length);
                Down message;

                try {
                    message = ProtocolJson.DecodeDown(text);
                } catch (JsonException e) {
                    Log(Level.Warn, $"ignoring unreadable frame: {e.Message}", e);
                    continue;
                }

                await DispatchAsync(message, ct).ConfigureAwait(false);
            }
        } finally {
            Connected.Reset();
            socket = null;
        }
    }

    private async Task PublishLoopAsync(CancellationToken ct) {
        await Ready.WaitAsync(ct).ConfigureAwait(false);

        while (!ct.IsCancellationRequested) {
            try {
                var state = await StateAsync(ct).ConfigureAwait(false);

                if (state != null && state.State.Count > 0) {
                    await PublishAsync(state).ConfigureAwait(false);
                }
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            } catch (Exception e) {
                // keep publishing even if one read fails
                Log(Level.Warn, $"state: {e.Message}", e);
            }

            await Task.Delay(StateInterval, ct).ConfigureAwait(false);
        }
    }

    // ---- sending --------------------------------------------------------------------------------

    /// <summary>Send property values now, outside the <see cref="StateAsync"/> cadence.</summary>
    public Task PublishAsync(StateMsg state) {
        if (state.T == 0) {
            state.T = Now();
        }

        return SendAsync(state);
    }

    /// <summary>Convenience: one entity's properties.</summary>
    public Task PublishAsync(string entityId, IEnumerable<KeyValuePair<string, Value>> properties) {
        var m = new StateMsg(Now());

        foreach (var (property, value) in properties) {
            m.Set(entityId, property, value);
        }

        return SendAsync(m);
    }

    public Task EventAsync(string message, Level level = Level.Info) => SendAsync(new Event { T = Now(), Level = level, Message = message });

    private async Task SendAsync(Up message) {
        var ws = socket;

        if (ws == null || ws.State != WebSocketState.Open) {
            Backlog(message);
            return;
        }

        try {
            await SendRawAsync(ws, message, stopping.Token).ConfigureAwait(false);
        } catch (Exception) when (!stopping.IsCancellationRequested) {
            Backlog(message);
        }
    }

    private void Backlog(Up message) {
        if (message is not (StateMsg or Event)) {
            return;
        }

        lock (gate) {
            while (backlog.Count >= BacklogLimit) {
                backlog.Dequeue();
            }

            backlog.Enqueue(message);
        }
    }

    private async Task SendRawAsync(ClientWebSocket ws, Up message, CancellationToken ct) {
        var bytes = Encoding.UTF8.GetBytes(ProtocolJson.Encode(message));
        await sendLock.WaitAsync(ct).ConfigureAwait(false);

        try {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        } finally {
            sendLock.Release();
        }
    }

    // ---- receiving ------------------------------------------------------------------------------

    private async Task DispatchAsync(Down message, CancellationToken ct) {
        switch (message) {
            case Welcome w:
                ServerTimeOffset = w.ServerTime - Now();
                Manifest = w.Manifest;
                // bind before anyone waiting on Ready (the publish loop, tasks, hosts) proceeds
                await OnManifestAsync(Manifest, ct).ConfigureAwait(false);
                Ready.Set();
                break;

            case ManifestUpdate m:
                Manifest = m.Manifest;
                await OnManifestAsync(Manifest, ct).ConfigureAwait(false);
                break;

            case TaskAssign assign:
                StartTask(assign.Task);
                break;

            case TaskCancel cancel:
                TaskHandle? handle;

                lock (gate) {
                    handle = tasks.TryGetValue(cancel.TaskId, out var entry) ? entry.Handle : null;
                }

                if (handle != null) {
                    handle.Cancellation.Cancel();
                } else {
                    var m = new StateMsg(Now());
                    m.Set(cancel.TaskId, Channels.TaskStatus, ObservationStatus.Cancelled.AsString());
                    m.Set(cancel.TaskId, Channels.TaskMessage, "cancelled");
                    await PublishAsync(m).ConfigureAwait(false);
                }

                break;

            case Plan plan:
                await OnPlanAsync(plan.Instrument, plan.Order, ct).ConfigureAwait(false);
                break;

            case Command command:
                _ = Task.Run(() => HandleCommandAsync(command, ct), CancellationToken.None);
                break;
        }
    }

    private async Task HandleCommandAsync(Command command, CancellationToken ct) {
        Ack ack;

        try {
            var note = await OnCommandAsync(command, ct).ConfigureAwait(false);
            ack = new Ack { CommandId = command.Id, Ok = true, Message = note, T = Now() };
        } catch (NotSupportedException) {
            ack = new Ack { CommandId = command.Id, Ok = false, Message = $"{command.Kind} not supported", T = Now() };
        } catch (Exception e) {
            ack = new Ack { CommandId = command.Id, Ok = false, Message = e.Message, T = Now() };
        }

        await SendAsync(ack).ConfigureAwait(false);
    }

    private void StartTask(ObservationTask task) {
        lock (gate) {
            if (tasks.ContainsKey(task.Id)) {
                return;
            }

            var handle = new TaskHandle(task, this, stopping.Token);
            var run = Task.Run(() => RunTaskWrapperAsync(handle), CancellationToken.None);
            tasks[task.Id] = (handle, run);
        }
    }

    private async Task RunTaskWrapperAsync(TaskHandle handle) {
        var id = handle.Task.Id;
        ObservationStatus status;
        string message;

        try {
            await Ready.WaitAsync(handle.Cancelled).ConfigureAwait(false);

            if (!AcceptTask(handle.Task, out var reason)) {
                await PublishAsync(StateMsg.TaskUpdate(id, ObservationStatus.Skipped, null, reason ?? "declined by the agent", Now())).ConfigureAwait(false);

                lock (gate) {
                    tasks.Remove(id);
                }

                handle.Cancellation.Dispose();
                return;
            }

            await PublishAsync(StateMsg.TaskUpdate(id, ObservationStatus.Running, 0.0, "starting", Now())).ConfigureAwait(false);
            await RunTaskAsync(handle).ConfigureAwait(false);
            (status, message) = handle.IsCancelled ? (ObservationStatus.Cancelled, "cancelled") : (ObservationStatus.Completed, "done");
        } catch (OperationCanceledException) when (handle.IsCancelled) {
            (status, message) = (ObservationStatus.Cancelled, stopping.IsCancellationRequested ? "agent stopped" : "cancelled");
        } catch (Exception e) {
            Log(Level.Error, $"task {id} failed: {e.Message}", e);
            (status, message) = (ObservationStatus.Failed, e.Message);
        }

        var final = StateMsg.TaskUpdate(id, status, status == ObservationStatus.Completed ? 1.0 : null, message, Now());
        await PublishAsync(final).ConfigureAwait(false);

        lock (gate) {
            tasks.Remove(id);
        }

        handle.Cancellation.Dispose();
    }

    /// <summary>Fire every running task's <see cref="TaskHandle.Cancelled"/>; they report <c>cancelled</c> as they unwind.</summary>
    protected void CancelRunningTasks() {
        List<TaskHandle> running;

        lock (gate) {
            running = tasks.Values.Select(t => t.Handle).ToList();
        }

        foreach (var handle in running) {
            handle.Cancellation.Cancel();
        }
    }

    /// <summary>Ids of the tasks this agent is currently running.</summary>
    public IReadOnlyList<string> RunningTasks {
        get {
            lock (gate) {
                return tasks.Keys.ToList();
            }
        }
    }

    private async Task CancelAllAsync() {
        List<(TaskHandle Handle, Task Run)> running;

        lock (gate) {
            running = tasks.Values.ToList();
        }

        foreach (var (handle, _) in running) {
            handle.Cancellation.Cancel();
        }

        try {
            await Task.WhenAll(running.Select(r => r.Run)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        } catch (Exception) {
            // tasks that do not honour cancellation are abandoned
        }
    }
}

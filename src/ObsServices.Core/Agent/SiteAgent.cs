using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ObsServices.Core.Imaging;
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
    private readonly HashSet<string> uploading = new();
    private readonly Queue<(double T, long Bytes)> sent = new();
    private readonly List<Task> grants = new();
    private ClientWebSocket? socket;
    private HttpClient? uploadHttp;

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
    /// TCP gives up. Enforced on .NET 9 and later; on .NET 8 the runtime has no such timeout and a
    /// half-open socket is only noticed when TCP reports it.
    /// </summary>
    public TimeSpan KeepAliveTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Why the last connection attempt or session ended, for status displays. Null while healthy.</summary>
    public string? LastError { get; private set; }

    /// <summary>Connection attempts that failed since the last successful one.</summary>
    public int ConsecutiveFailures { get; private set; }

    /// <summary>Telemetry messages kept for replay while the service is unreachable.</summary>
    public int BacklogLimit { get; set; } = 20_000;

    /// <summary>
    /// The frames this agent holds for the service. Attach one before <see cref="RunAsync"/> to announce
    /// frames (<see cref="AnnounceImageAsync(byte[], FrameInfo)"/>), honour upload grants and report the
    /// inventory after every reconnect; <c>hello</c> then declares <c>images:spool</c>. Null: no images.
    /// </summary>
    public FrameSpool? Spool { get; set; }

    /// <summary>How many times an upload is retried through network trouble (5xx, resets, timeouts).</summary>
    public int UploadAttempts { get; set; } = 5;

    /// <summary>Per-request timeout of the upload client. A chunk is 8 MiB at most, so two minutes covers a slow link.</summary>
    public TimeSpan UploadTimeout { get; set; } = TimeSpan.FromMinutes(2);

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
    /// Run a task that carries a program (<c>handle.Task.Program</c>, body included). Called instead
    /// of <see cref="RunTaskAsync"/> for those. The default declines, which reports <c>skipped</c>;
    /// the service only sends program tasks to agents whose <see cref="Capabilities"/> list
    /// <c>program:&lt;format&gt;</c>.
    /// </summary>
    protected virtual Task RunProgramAsync(TaskHandle handle) =>
        throw new NotSupportedException($"this agent does not run {handle.Task.Program?.Format} programs");

    /// <summary>
    /// The service wants properties of an entity set (several at once when they belong together,
    /// like <c>ra_deg</c> and <c>dec_deg</c>). For a move that takes time return
    /// <see cref="SetReply.Accepted"/> and call <see cref="SetDoneAsync"/> or <see cref="SetFailedAsync"/>
    /// with the request id afterwards. Throw to report <c>rejected</c> with the exception's text.
    /// </summary>
    protected virtual Task<SetReply> OnSetAsync(string requestId, string entity, IReadOnlyDictionary<string, Value> values, CancellationToken cancellationToken) =>
        Task.FromResult(SetReply.Unsupported("this agent does not set properties"));

    /// <summary>
    /// Decide whether to take a task at all, before it is reported running. Return false with a
    /// reason to have it reported as <c>skipped</c> (the service hands it to another agent or leaves
    /// it to the operator). Default: accept everything.
    /// </summary>
    protected virtual bool AcceptTask(ObservationTask task, out string? reason) {
        if (task.Program is { } program && !Capabilities.Contains(ProgramRef.Capability(program.Format))) {
            reason = $"this agent does not run {program.Format} programs";
            return false;
        }

        // the service translates moving targets for agents that do not list the kind; one that arrives
        // untranslated is one this agent cannot follow
        if (task.Target.Body is { } body && !Capabilities.Contains(Body.Capability(body.Kind))) {
            reason = $"this agent does not follow {body.Kind} targets";
            return false;
        }

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

    /// <summary>
    /// The service wants these frames (hook). The default uploads every granted hash the spool holds,
    /// <c>max_concurrency</c> at a time, in the background, retrying through network trouble; override to
    /// schedule uploads yourself (<see cref="FrameUploader.UploadAsync"/> does one).
    /// </summary>
    protected virtual Task OnImageGrantAsync(ImageGrant grant) => RunGrantAsync(grant);

    /// <summary>
    /// The service holds these frames durably (hook, default no-op). Called with the entries still in the
    /// spool; afterwards the spool forgets them and deletes the files it owns when allowed.
    /// </summary>
    protected virtual Task OnImageReleasedAsync(IReadOnlyList<SpoolEntry> entries) => Task.CompletedTask;

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
#if NET9_0_OR_GREATER
                ws.Options.KeepAliveTimeout = KeepAliveTimeout;
#endif
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

        uploadHttp?.Dispose();
        uploadHttp = null;
    }

    /// <summary>Ask <see cref="RunAsync"/> to return. Running tasks see their <see cref="TaskHandle.Cancelled"/> fire.</summary>
    public void Stop() => stopping.Cancel();

    public bool IsStopping => stopping.IsCancellationRequested;

    /// <summary>A refused handshake names its HTTP status, so a bad token reads differently from a down server.</summary>
    private static string DescribeFailure(ClientWebSocket ws, Exception e) {
        var status = ws.HttpStatusCode;

        if (status != 0 && (int)status != 101) {
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
        var capabilities = Capabilities.ToList();

        if (Spool != null && !capabilities.Contains(Images.Capability)) {
            capabilities.Add(Images.Capability);
        }

        var hello = new Hello { Agent = Name, Version = Version, Protocol = ProtocolJson.Version, Capabilities = capabilities, Entities = Entities().ToList() };
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
                state = AddUploaderState(state);

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

    /// <summary>
    /// Add or annotate entities after <c>hello</c>: for a known entity id the properties are merged
    /// (a wheel's real filter list as <c>values</c>, what the agent can set as <c>writable</c>); a new
    /// id adds a device. The service keeps the declaration with the site.
    /// </summary>
    public Task DeclareEntitiesAsync(IEnumerable<Entity> entities) => SendAsync(new EntitiesMsg { Entities = entities.ToList() });

    /// <summary>Second answer to a set that was accepted: it got there.</summary>
    public Task SetDoneAsync(string requestId, string? message = null, Dictionary<string, Value>? observed = null) =>
        SendAsync(Ack.ForSet(requestId, SetStatus.Done, message, observed, Now()));

    /// <summary>Second answer to a set that was accepted: it did not get there.</summary>
    public Task SetFailedAsync(string requestId, string message) => SendAsync(Ack.ForSet(requestId, SetStatus.Rejected, message, null, Now()));

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
        // the same kinds the service's Python agent replays after an outage
        if (message is not (StateMsg or Event or ImageCaptured)) {
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

                if (Spool != null) {
                    // what we still hold; the service grants or releases each frame accordingly
                    await SendInventoryAsync().ConfigureAwait(false);
                }

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

            case Command command when command.Kind == CommandKinds.Set:
                _ = Task.Run(() => HandleSetAsync(command, ct), CancellationToken.None);
                break;

            case Command command:
                _ = Task.Run(() => HandleCommandAsync(command, ct), CancellationToken.None);
                break;

            case ImageGrant grant when Spool != null:
                StartGrant(grant);
                break;

            case ImageRelease release when Spool != null:
                await HandleReleaseAsync(release).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleSetAsync(Command command, CancellationToken ct) {
        SetReply reply;

        try {
            reply = await OnSetAsync(command.Id, command.SetEntity ?? string.Empty, command.SetValues(), ct).ConfigureAwait(false);
        } catch (NotSupportedException e) {
            reply = SetReply.Unsupported(e.Message);
        } catch (Exception e) {
            reply = SetReply.Rejected(e.Message);
        }

        await SendAsync(Ack.ForSet(command.Id, reply.Status, reply.Message, reply.Observed, Now())).ConfigureAwait(false);
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

            if (handle.Task.Program != null) {
                await RunProgramAsync(handle).ConfigureAwait(false);
            } else {
                await RunTaskAsync(handle).ConfigureAwait(false);
            }

            (status, message) = handle.IsCancelled ? (ObservationStatus.Cancelled, "cancelled") : (ObservationStatus.Completed, "done");
        } catch (OperationCanceledException) when (handle.IsCancelled) {
            (status, message) = (ObservationStatus.Cancelled, stopping.IsCancellationRequested ? "agent stopped" : "cancelled");
        } catch (NotSupportedException e) {
            (status, message) = (ObservationStatus.Skipped, e.Message);
        } catch (Exception e) {
            Log(Level.Error, $"task {id} failed: {e.Message}", e);
            (status, message) = (ObservationStatus.Failed, e.Message);
        }

        handle.Closed = true;
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
        List<Task> uploads;

        lock (gate) {
            running = tasks.Values.ToList();
            uploads = grants.ToList();
        }

        foreach (var (handle, _) in running) {
            handle.Cancellation.Cancel();
        }

        try {
            await Task.WhenAll(running.Select(r => r.Run).Concat(uploads)).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        } catch (Exception) {
            // tasks that do not honour cancellation are abandoned; uploads resume under the next grant
        }
    }

    // ---- images: the spool --------------------------------------------------------------------------

    /// <summary>
    /// A frame exists: describe it (pixel hash, header, statistics, a preview), keep a copy in the
    /// <see cref="Spool"/> and tell the service (<c>image_captured</c>). <paramref name="info"/> must name the
    /// camera entity. Queued and replayed like telemetry when the socket is down. The upload happens later,
    /// only when the service grants it. Returns the pixel hash.
    /// </summary>
    public Task<string> AnnounceImageAsync(byte[] fits, FrameInfo info) => AnnounceImageAsync(FrameDescriber.Describe(fits, info), fits);

    /// <summary>The same for an unsigned 16-bit frame still in memory, with <paramref name="fits"/> its encoding (no second decode).</summary>
    public Task<string> AnnounceImageAsync(ReadOnlyMemory<ushort> pixels, int width, int height, byte[] fits, FrameInfo info) =>
        AnnounceImageAsync(FrameDescriber.Describe(pixels, width, height, fits, info), fits);

    /// <summary>Announce an already described frame, spooling a copy of <paramref name="fits"/>.</summary>
    public async Task<string> AnnounceImageAsync(ImageCaptured described, byte[] fits) {
        var spool = Spool ?? throw new InvalidOperationException("no frame spool is attached to this agent");
        RequireAnnouncable(described);
        await Task.Run(() => spool.Add(described, fits)).ConfigureAwait(false);
        await SendAsync(described).ConfigureAwait(false);
        return described.Hash;
    }

    /// <summary>Announce a FITS file the host keeps itself; the spool references it and never deletes it.</summary>
    public async Task<string> AnnounceImageFileAsync(string path, FrameInfo info) {
        var spool = Spool ?? throw new InvalidOperationException("no frame spool is attached to this agent");
        var fits = await File.ReadAllBytesAsync(path).ConfigureAwait(false);
        info.FileName ??= Path.GetFileName(path);
        var described = FrameDescriber.Describe(fits, info);
        RequireAnnouncable(described);
        spool.AddReference(described, path);
        await SendAsync(described).ConfigureAwait(false);
        return described.Hash;
    }

    private static void RequireAnnouncable(ImageCaptured described) {
        if (string.IsNullOrEmpty(described.Camera)) {
            throw new ArgumentException("a frame needs the camera entity it came from");
        }

        if (!Images.IsHash(described.Hash)) {
            throw new ArgumentException("the frame has no pixel hash");
        }
    }

    /// <summary>Tell the service what the spool holds (<c>image_inventory</c>). Sent after every <c>welcome</c>.</summary>
    public Task SendInventoryAsync() {
        var spool = Spool;

        if (spool == null) {
            return Task.CompletedTask;
        }

        var inventory = new ImageInventory {
            Frames = spool.Entries.Select(e => new InventoryFrame { Hash = e.Hash, Bytes = e.Bytes }).ToList(),
            FreeBytes = spool.FreeBytes(),
        };
        return SendAsync(inventory);
    }

    /// <summary>Bytes per second uploaded over the last <paramref name="windowSeconds"/>.</summary>
    public double UploadRateBps(double windowSeconds = 60) {
        var cut = Now() - windowSeconds;

        lock (gate) {
            while (sent.Count > 0 && sent.Peek().T < cut) {
                sent.Dequeue();
            }

            return sent.Sum(s => s.Bytes) / windowSeconds;
        }
    }

    /// <summary>Hashes whose upload is in progress.</summary>
    public IReadOnlyList<string> Uploading {
        get { lock (gate) return uploading.ToList(); }
    }

    /// <summary>
    /// The <c>uploader</c> entities' telemetry (nothing when the site has no such component): queued frames
    /// and bytes, free space, upload rate over the last minute, age of the oldest frame waiting. Merged into
    /// every state message.
    /// </summary>
    private StateMsg? AddUploaderState(StateMsg? state) {
        var spool = Spool;

        if (spool == null) {
            return state;
        }

        var uploaders = Manifest.OfKind(EntityKind.Component).Where(e => e.Component == Channels.UploaderComponent).ToList();

        if (uploaders.Count == 0) {
            return state;
        }

        var pending = spool.Pending;
        var now = Now();
        state ??= new StateMsg(now);

        foreach (var u in uploaders) {
            state.Set(u.Id, Channels.UploaderQueuedFrames, pending.Count);
            state.Set(u.Id, Channels.UploaderQueuedBytes, pending.Sum(e => e.Bytes));
            state.Set(u.Id, Channels.UploaderRateBps, Math.Round(UploadRateBps(), 1));
            state.Set(u.Id, Channels.UploaderOldestUnsentS, pending.Count == 0 ? 0.0 : Math.Round(Math.Max(0.0, now - pending.Min(e => e.CapturedAt)), 1));

            if (spool.FreeBytes() is { } free) {
                state.Set(u.Id, Channels.UploaderFreeBytes, free);
            }
        }

        return state;
    }

    private void StartGrant(ImageGrant grant) {
        var run = Task.Run(async () => {
            try {
                await OnImageGrantAsync(grant).ConfigureAwait(false);
            } catch (OperationCanceledException) {
            } catch (Exception e) {
                Log(Level.Warn, $"image grant: {e.Message}", e);
            }
        }, CancellationToken.None);

        lock (gate) {
            grants.Add(run);
        }

        run.ContinueWith(t => { lock (gate) grants.Remove(t); }, TaskScheduler.Default);
    }

    private async Task RunGrantAsync(ImageGrant grant) {
        var spool = Spool;

        if (spool == null) {
            return;
        }

        List<string> wanted;

        lock (gate) {
            wanted = grant.Hashes.Where(h => spool.Get(h) is { Uploaded: false } && uploading.Add(h)).ToList();
        }

        if (wanted.Count == 0) {
            return;
        }

        try {
            var http = uploadHttp ??= new HttpClient { Timeout = UploadTimeout };
            using var sem = new SemaphoreSlim(Math.Max(1, grant.MaxConcurrency));
            await Task.WhenAll(wanted.Select(h => UploadOneAsync(http, sem, spool, grant, h))).ConfigureAwait(false);
        } finally {
            lock (gate) {
                uploading.ExceptWith(wanted);
            }
        }
    }

    private async Task UploadOneAsync(HttpClient http, SemaphoreSlim sem, FrameSpool spool, ImageGrant grant, string hash) {
        var ct = stopping.Token;
        var brief = hash[..12] + "…";
        await sem.WaitAsync(ct).ConfigureAwait(false);

        try {
            for (var attempt = 1; ; attempt++) {
                var entry = spool.Get(hash);

                if (entry == null || entry.Uploaded) {
                    return;
                }

                if (grant.ExpiresAt > 0 && Now() > grant.ExpiresAt) {
                    await EventAsync($"image upload: the grant for {brief} expired before it was sent", Level.Warn).ConfigureAwait(false);
                    return;
                }

                UploadResult result;

                try {
                    using var data = spool.Open(entry);
                    result = await FrameUploader.UploadAsync(http, grant, hash, data, NoteSent, ct).ConfigureAwait(false);
                } catch (IOException e) {
                    await EventAsync($"image upload: cannot read {brief}: {e.Message}", Level.Warn).ConfigureAwait(false);
                    return;
                } catch (Exception e) when (e is HttpRequestException || (e is OperationCanceledException && !ct.IsCancellationRequested)) {
                    if (attempt >= UploadAttempts) {
                        await EventAsync($"image upload failed: {brief} after {attempt} attempts: {e.Message}", Level.Warn).ConfigureAwait(false);
                        return;
                    }

                    Log(Level.Warn, $"image upload {brief}: {e.Message}; retrying", e);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, attempt - 1))), ct).ConfigureAwait(false);
                    continue;
                }

                switch (result.Status) {
                    case UploadStatus.Received:
                        spool.MarkUploaded(hash);
                        Log(Level.Info, $"image {brief} received by the service ({entry.Bytes} bytes)");
                        break;
                    case UploadStatus.Missing:
                        await EventAsync($"image upload skipped: the service has no grant for {brief}", Level.Warn).ConfigureAwait(false);
                        break;
                    default:
                        await EventAsync($"image upload refused: {brief} {result.Error} (HTTP {result.Http})", Level.Warn).ConfigureAwait(false);
                        break;
                }

                return;
            }
        } finally {
            sem.Release();
        }
    }

    private void NoteSent(long bytes) {
        lock (gate) {
            sent.Enqueue((Now(), bytes));
        }
    }

    private async Task HandleReleaseAsync(ImageRelease release) {
        var spool = Spool;

        if (spool == null) {
            return;
        }

        var entries = release.Hashes.Select(spool.Get).Where(e => e != null).Select(e => e!).ToList();

        if (entries.Count == 0) {
            return;
        }

        try {
            await OnImageReleasedAsync(entries).ConfigureAwait(false);
        } catch (Exception e) {
            Log(Level.Warn, $"image release hook: {e.Message}", e);
        }

        foreach (var e in entries) {
            spool.Release(e.Hash);
        }
    }
}

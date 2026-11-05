using ObsServices.Core.Devices;
using ObsServices.Core.Protocol;

namespace ObsServices.Core.Agent;

/// <summary>Settings of the generic agent. Read on use, so a host may change them while the agent runs.</summary>
public sealed class ObservatoryAgentOptions {
    /// <summary>Instrument to drive: id, name or mount instance name. Null means the site's first.</summary>
    public string? InstrumentKey { get; set; }

    /// <summary>The site's <c>sequencer</c> instance to publish to (id, instance or name). Null means the first.</summary>
    public string? SequencerKey { get; set; }

    /// <summary>
    /// Publish state but never act: tasks are reported back as <c>skipped</c> and commands are
    /// refused in their ack, with the reason. The safe default for a host that watches real hardware.
    /// </summary>
    public bool TelemetryOnly { get; set; }

    /// <summary>Joint value published for the roof joints when the roof is open (millimetres of travel).</summary>
    public double RoofOpenJointValue { get; set; } = 2400;

    /// <summary>Enable tracking after a slew (mounts usually do this themselves).</summary>
    public bool EnsureTrackingAfterSlew { get; set; } = true;

    /// <summary>Close the roof when a <c>safe</c> command arrives.</summary>
    public bool CloseRoofOnSafe { get; set; } = true;
}

/// <summary>
/// The generic site agent: maps a <see cref="IDeviceSet"/> onto the site's entities and carries out
/// observation tasks with it. Hosts (the N.I.N.A. plugin, a simulator) only implement the device
/// interfaces. One instrument is driven; tasks run one at a time in arrival order.
/// </summary>
public class ObservatoryAgent : SiteAgent {
    private readonly IDeviceSet devices;
    private readonly ObservatoryAgentOptions options;
    private readonly SemaphoreSlim instrument = new(1, 1);
    private readonly object gate = new();
    private (double Ra, double Dec)? target;

    public ObservatoryAgent(string baseUrl, string siteId, string token, IDeviceSet devices, ObservatoryAgentOptions? options = null) : base(baseUrl, siteId, token) {
        this.devices = devices;
        this.options = options ?? new ObservatoryAgentOptions();
        Capabilities.AddRange(new[] { "mount", "camera", "roof" });

        if (devices.Sequencer != null) {
            Capabilities.AddRange(devices.Sequencer.Formats.Select(ProgramRef.Capability));
        }

        if (this.options.TelemetryOnly) {
            Capabilities.Add(TelemetryOnlyCapability);
        }
    }

    /// <summary>Advertised in <c>hello</c> when the agent starts in telemetry-only mode.</summary>
    public const string TelemetryOnlyCapability = "telemetry_only";

    public const string TelemetryOnlyReason = "agent is in telemetry-only mode";

    /// <summary>Live view of the option; hosts toggle it through the options object they passed in.</summary>
    public bool TelemetryOnly => options.TelemetryOnly;

    /// <summary>How the manifest was bound to the devices; empty until <c>welcome</c>.</summary>
    public SiteBinding Binding { get; private set; } = SiteBinding.Empty;

    /// <summary>The J2000 target the mount was last asked to point at, for the aim reticle.</summary>
    public (double Ra, double Dec)? CurrentTarget {
        get { lock (gate) return target; }
        private set { lock (gate) target = value; }
    }

    protected override async Task OnManifestAsync(Manifest manifest, CancellationToken cancellationToken) {
        Binding = SiteBinding.FromManifest(manifest, options.InstrumentKey, options.SequencerKey);

        if (Binding.MountEntity == null) {
            Log(Level.Warn, options.InstrumentKey == null ? "the site has no instrument; nothing to drive" : $"instrument `{options.InstrumentKey}` is not in the site");
        }

        if (devices.Sequencer != null && Binding.SequencerEntity == null) {
            Log(Level.Warn, "the site has no `sequencer` component; the sequencer's state has nowhere to go (add one to the site's assembly)");
        }

        var declared = DeclaredEntities().ToList();

        if (declared.Count > 0) {
            await DeclareEntitiesAsync(declared).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// What this agent tells the service about the bound entities beyond the library's declaration:
    /// the wheel's real filter list, and which properties a <c>set</c> may write. The library marks
    /// nothing writable; only an agent that can act says so.
    /// </summary>
    protected virtual IEnumerable<Entity> DeclaredEntities() {
        var b = Binding;

        if (b.MountEntity != null && devices.Mount != null) {
            var props = new List<PropertyInfo> {
                PropertyInfo.Number(Channels.MountRaDeg, "deg", "Sky right ascension to slew to (with dec_deg)").AsWritable(),
                PropertyInfo.Number(Channels.MountDecDeg, "deg", "Sky declination to slew to (with ra_deg)").AsWritable(),
                PropertyInfo.Text(Channels.MountMode, new[] { Channels.MountModeParked, Channels.MountModeSlewing, Channels.MountModeTracking, Channels.MountModeStopped }, "Set parked, stopped or tracking").AsWritable(),
            };
            yield return new Entity { Id = b.MountEntity, Kind = EntityKind.Component, Properties = props };
        }

        if (b.FilterWheelEntity != null && devices.FilterWheel is { } wheel) {
            var filter = PropertyInfo.Text(Channels.Filter, wheel.Filters, "Filter in the light path").AsWritable();
            yield return new Entity { Id = b.FilterWheelEntity, Kind = EntityKind.Component, Properties = { filter } };
        }

        if (b.FocuserEntity != null && devices.Focuser != null) {
            yield return new Entity { Id = b.FocuserEntity, Kind = EntityKind.Component, Properties = { PropertyInfo.Number(Channels.Focus, "", "Focuser position").AsWritable() } };
        }

        if (b.RoofEntity != null && devices.Roof != null) {
            var roof = new Entity { Id = b.RoofEntity, Kind = EntityKind.Component };

            foreach (var joint in b.RoofJoints) {
                roof.Properties.Add(PropertyInfo.Number(joint, "mm", "Roof travel: 0 closes, the open value opens").AsWritable());
            }

            yield return roof;
        }

        if (b.SequencerEntity != null && devices.Sequencer != null) {
            yield return new Entity {
                Id = b.SequencerEntity,
                Kind = EntityKind.Component,
                Properties = {
                    new PropertyInfo { Name = Channels.SequencerTelemetryOnly, Kind = PropertyKind.Bool, Description = "Switch the agent between reporting only and full control" }.AsWritable(),
                    PropertyInfo.Text(Channels.SequencerState, new[] { Channels.SequencerStateIdle }, "Set idle to stop the running program").AsWritable(),
                },
            };
        }
    }

    // ---- telemetry --------------------------------------------------------------------------------

    protected override Task<StateMsg?> StateAsync(CancellationToken cancellationToken) {
        var b = Binding;
        var m = new StateMsg(Now());
        var mount = devices.Mount?.Read();

        if (mount is { Connected: true, SiderealTimeDeg: { } lst }) {
            m.Set(SiteEntityId, Channels.SiteLstDeg, lst);
        }

        if (b.MountEntity != null && mount is { Connected: true }) {
            PublishMount(m, b, mount);
        }

        if (b.CameraEntity != null && devices.Camera?.Read() is { Connected: true } cam) {
            m.Set(b.CameraEntity, Channels.CameraState, cam.Activity switch {
                CameraActivity.Exposing => Channels.CameraStateExposing(devices.FilterWheel?.CurrentFilter ?? "L"),
                CameraActivity.ReadingOut => Channels.CameraStateReadingOut,
                _ => Channels.CameraStateIdle,
            });
            m.Set(b.CameraEntity, Channels.CameraExposureS, cam.ExposureSeconds);
            m.Set(b.CameraEntity, Channels.CameraExposureRemainingS, cam.ExposureRemainingSeconds);

            if (cam.TemperatureC is { } t) {
                m.Set(b.CameraEntity, Channels.CameraTemperatureC, t);
            }
        }

        if (b.FilterWheelEntity != null && devices.FilterWheel is { Connected: true, CurrentFilter: { } filter }) {
            m.Set(b.FilterWheelEntity, Channels.Filter, filter);
        }

        if (b.FocuserEntity != null && devices.Focuser is { Connected: true, Position: { } position }) {
            m.Set(b.FocuserEntity, Channels.Focus, position);
        }

        if (b.SequencerEntity != null && devices.Sequencer is { } sequencer) {
            var s = sequencer.Read();
            var e = b.SequencerEntity;
            m.Set(e, Channels.SequencerState, !s.Connected ? Channels.SequencerStateDisconnected : s.Running ? Channels.SequencerStateRunning : Channels.SequencerStateIdle);
            m.Set(e, Channels.SequencerTelemetryOnly, options.TelemetryOnly);

            if (s.Connected) {
                m.Set(e, Channels.SequencerSoftware, s.Software);
                m.Set(e, Channels.SequencerVersion, s.Version);
                m.Set(e, Channels.SequencerProfile, s.Profile ?? string.Empty);
                m.Set(e, Channels.SequencerProgram, s.Program ?? string.Empty);
                m.Set(e, Channels.SequencerRunningItem, s.RunningItem ?? string.Empty);
                m.Set(e, Channels.SequencerExtensions, string.Join(",", s.Extensions));
                m.Set(e, Channels.SequencerInstrument, b.InstrumentId ?? string.Empty);
            }
        }

        if (b.RoofEntity != null && devices.Roof is { Connected: true } roof) {
            var activity = roof.Read();
            m.Set(b.RoofEntity, Channels.RoofState, activity switch {
                RoofActivity.Open => Channels.RoofOpen,
                RoofActivity.Opening => Channels.RoofOpening,
                RoofActivity.Closing => Channels.RoofClosing,
                RoofActivity.Closed => Channels.RoofClosed,
                _ => Channels.RoofClosed,
            });

            foreach (var joint in b.RoofJoints) {
                m.Set(b.RoofEntity, joint, activity == RoofActivity.Open ? options.RoofOpenJointValue : 0.0);
            }
        }

        return Task.FromResult<StateMsg?>(m.State.Count == 0 ? null : m);
    }

    private void PublishMount(StateMsg m, SiteBinding b, MountReading mount) {
        var entity = b.MountEntity!;

        if (b.IsAltAz) {
            if (mount.AzimuthDeg is { } az && mount.AltitudeDeg is { } alt) {
                m.Set(entity, b.HaJoint, az);
                m.Set(entity, b.DecJoint, alt);
            }
        } else if (mount.SiderealTimeDeg is { } lst && mount.RightAscensionDeg is { } ra && mount.DeclinationDeg is { } dec) {
            var ha = Astro.HourAngleDeg(lst, ra);

            if (b.IsGermanEquatorial) {
                // The joints are axis angles: the flipped pose turns both through 180°. Trust the
                // driver's side; when it cannot say, assume the usual choice (eastern targets on the
                // west side, so flipped while east of the meridian).
                var tubeWest = mount.PierSide switch {
                    PierSide.West => true,
                    PierSide.East => false,
                    _ => ha < 0.0,
                };
                var (raAxis, decAxis) = Astro.GemAxes(ha, dec, tubeWest);
                m.Set(entity, b.HaJoint, raAxis);
                m.Set(entity, b.DecJoint, decAxis);
                m.Set(entity, Channels.MountPierSide, tubeWest ? Channels.PierWest : Channels.PierEast);
            } else {
                m.Set(entity, b.HaJoint, ha);
                m.Set(entity, b.DecJoint, dec);
            }

            m.Set(entity, Channels.MountDecDeg, mount.DeclinationJ2000Deg ?? dec);
        }

        if (mount.RightAscensionJ2000Deg is { } raJ2000) {
            m.Set(entity, Channels.MountRaDeg, raJ2000);
        } else if (mount.RightAscensionDeg is { } raDate) {
            m.Set(entity, Channels.MountRaDeg, raDate);
        }

        m.Set(entity, Channels.MountMode, mount.State switch {
            MountState.Parked => Channels.MountModeParked,
            MountState.Slewing => Channels.MountModeSlewing,
            MountState.Tracking => Channels.MountModeTracking,
            _ => Channels.MountModeStopped,
        });

        if (CurrentTarget is { } t && mount.AltitudeDeg != null && mount.State is MountState.Tracking or MountState.Slewing) {
            // the aim reticle: where the mount was told to look (its own alt/az while tracking)
            m.Set(entity, Channels.MountTargetAltDeg, mount.AltitudeDeg.Value);

            if (mount.AzimuthDeg is { } az) {
                m.Set(entity, Channels.MountTargetAzDeg, az);
            }
        }
    }

    // ---- tasks ------------------------------------------------------------------------------------

    protected override bool AcceptTask(ObservationTask task, out string? reason) {
        if (options.TelemetryOnly) {
            reason = TelemetryOnlyReason;
            return false;
        }

        return base.AcceptTask(task, out reason);
    }

    protected override async Task RunTaskAsync(TaskHandle handle) {
        var task = handle.Task;
        var ct = handle.Cancelled;
        var mount = devices.Mount ?? throw new InvalidOperationException("no mount device");
        var camera = devices.Camera ?? throw new InvalidOperationException("no camera device");

        if (Binding.InstrumentId != null && task.Instrument != null && task.Instrument != Binding.InstrumentId) {
            throw new InvalidOperationException($"task is for instrument {task.Instrument}; this agent drives {Binding.InstrumentId}");
        }

        await instrument.WaitAsync(ct).ConfigureAwait(false);

        try {
            if (!mount.Read().Connected) {
                throw new InvalidOperationException("mount is not connected");
            }

            if (!camera.Read().Connected) {
                throw new InvalidOperationException("camera is not connected");
            }

            await EnsureRoofOpenAsync(handle, ct).ConfigureAwait(false);

            if (mount.Read().State == MountState.Parked) {
                await handle.EventAsync("unparking mount").ConfigureAwait(false);
                await mount.UnparkAsync(ct).ConfigureAwait(false);
            }

            await handle.ProgressAsync(0.0, $"slewing to {task.Target.Name}").ConfigureAwait(false);
            CurrentTarget = (task.Target.RaDeg, task.Target.DecDeg);
            await mount.SlewAsync(task.Target.RaDeg, task.Target.DecDeg, ct).ConfigureAwait(false);

            if (options.EnsureTrackingAfterSlew && mount.Read().State != MountState.Tracking) {
                await mount.SetTrackingAsync(true, ct).ConfigureAwait(false);
            }

            var frames = task.Sequence.SelectMany(e => Enumerable.Repeat(e, Math.Max(0, e.Count))).ToList();
            var total = Math.Max(task.TotalExposureSeconds, double.Epsilon);
            var done = 0.0;
            await handle.FramesAsync(0, frames.Count).ConfigureAwait(false);

            for (var i = 0; i < frames.Count; i++) {
                ct.ThrowIfCancellationRequested();
                var frame = frames[i];
                await SelectFilterAsync(handle, frame.FilterFor(Binding.FilterWheelEntity), ct).ConfigureAwait(false);
                await handle.ProgressAsync(done / total, $"exposing {frame.Filter} {frame.Seconds:0.#}s ({i + 1}/{frames.Count})").ConfigureAwait(false);
                var request = new ExposureRequest(frame.Seconds, frame.Filter, task.Target.Name, task.Target.RaDeg, task.Target.DecDeg, task.Id, task.Name, i, frames.Count);
                await camera.ExposeAsync(request, ct).ConfigureAwait(false);
                done += frame.Seconds;
                await handle.FramesAsync(i + 1, frames.Count).ConfigureAwait(false);
                await handle.ProgressAsync(done / total, $"{i + 1}/{frames.Count} exposures").ConfigureAwait(false);
            }
        } finally {
            instrument.Release();
        }
    }

    /// <summary>A program task: handed to the sequencer host under the same one-instrument lock as a device task.</summary>
    protected override async Task RunProgramAsync(TaskHandle handle) {
        var program = handle.Task.Program ?? throw new InvalidOperationException("not a program task");
        var host = devices.Sequencer ?? throw new NotSupportedException($"this agent does not run {program.Format} programs");

        if (!host.Formats.Contains(program.Format)) {
            throw new NotSupportedException($"this agent does not run {program.Format} programs");
        }

        if (string.IsNullOrEmpty(program.Body)) {
            throw new InvalidOperationException($"program {program.Id} arrived without its body");
        }

        var ct = handle.Cancelled;
        await instrument.WaitAsync(ct).ConfigureAwait(false);

        try {
            if (!host.Read().Connected) {
                throw new InvalidOperationException("the sequencer is not available");
            }

            var total = handle.Task.TotalFrames;
            var totalSeconds = handle.Task.TotalExposureSeconds;
            await handle.ProgressAsync(0.0, $"running {program.Format} program {program.Id} (revision {program.Revision})").ConfigureAwait(false);

            if (total > 0) {
                await handle.FramesAsync(0, total).ConfigureAwait(false);
            }

            // reports are published in order and all of them before the final status, so a late
            // "3/3 frames" can never flip a completed task back to running
            var progress = new OrderedProgress(done => total > 0
                ? Task.WhenAll(handle.FramesAsync(done, total), handle.ProgressAsync(Math.Min(1.0, (double)done / total), $"{done}/{total} frames"))
                : Task.CompletedTask);
            await host.RunProgramAsync(program, handle.Task, progress, ct).ConfigureAwait(false);
            await progress.DrainAsync().ConfigureAwait(false);
        } finally {
            instrument.Release();
        }
    }

    /// <summary>An <see cref="IProgress{T}"/> that runs its async handler for each report in order and can be awaited to the end.</summary>
    private sealed class OrderedProgress : IProgress<int> {
        private readonly Func<int, Task> handler;
        private readonly object gate = new();
        private Task chain = Task.CompletedTask;

        public OrderedProgress(Func<int, Task> handler) {
            this.handler = handler;
        }

        public void Report(int value) {
            lock (gate) {
                chain = chain.ContinueWith(_ => handler(value), TaskScheduler.Default).Unwrap();
            }
        }

        public Task DrainAsync() {
            lock (gate) {
                return chain;
            }
        }
    }

    private async Task EnsureRoofOpenAsync(TaskHandle handle, CancellationToken ct) {
        var roof = devices.Roof;

        if (roof is not { Connected: true } || Binding.RoofEntity == null) {
            return;
        }

        if (roof.Read() == RoofActivity.Open) {
            return;
        }

        await handle.EventAsync("opening roof").ConfigureAwait(false);
        await roof.OpenAsync(ct).ConfigureAwait(false);
        await handle.EventAsync("roof open").ConfigureAwait(false);
    }

    private async Task SelectFilterAsync(TaskHandle handle, string? filter, CancellationToken ct) {
        var wheel = devices.FilterWheel;

        // null: the exposure names other wheels and leaves this one where it is
        if (filter == null || wheel is not { Connected: true }) {
            return;
        }

        if (wheel.CurrentFilter == filter) {
            return;
        }

        if (!wheel.Filters.Contains(filter)) {
            await handle.EventAsync($"filter `{filter}` is not in the wheel ({string.Join(", ", wheel.Filters)}); exposing through {wheel.CurrentFilter ?? "the current filter"}", Level.Warn).ConfigureAwait(false);
            return;
        }

        await wheel.ChangeFilterAsync(filter, ct).ConfigureAwait(false);
    }

    // ---- sets -------------------------------------------------------------------------------------

    /// <summary>
    /// Property writes mapped onto the devices, with the semantics of the service's virtual agent:
    /// a slew is <c>ra_deg</c> and <c>dec_deg</c> together (accepted, then done with the axes reached);
    /// <c>mode</c> parks, stops or tracks; a wheel takes <c>filter</c>; a focuser <c>focus</c>; a roof
    /// its travel joint; the sequencer <c>telemetry_only</c> and <c>state = idle</c>.
    /// </summary>
    protected override async Task<SetReply> OnSetAsync(string requestId, string entity, IReadOnlyDictionary<string, Value> values, CancellationToken cancellationToken) {
        var b = Binding;

        if (entity == b.SequencerEntity && b.SequencerEntity != null) {
            return await SetSequencerAsync(values, cancellationToken).ConfigureAwait(false);
        }

        if (options.TelemetryOnly) {
            return SetReply.Rejected(TelemetryOnlyReason);
        }

        if (entity == b.MountEntity && b.MountEntity != null) {
            return await SetMountAsync(requestId, values, cancellationToken).ConfigureAwait(false);
        }

        if (entity == b.FilterWheelEntity && b.FilterWheelEntity != null) {
            if (devices.FilterWheel is not { Connected: true } wheel) {
                return SetReply.Unbound("the filter wheel is not connected");
            }

            if (values.TryGetValue(Channels.Filter, out var f) && f.AsText() is { } filter) {
                if (!wheel.Filters.Contains(filter)) {
                    return SetReply.Rejected($"this wheel holds {string.Join("/", wheel.Filters)}, not `{filter}`");
                }

                await wheel.ChangeFilterAsync(filter, cancellationToken).ConfigureAwait(false);
                return SetReply.Done($"filter {filter}", Channels.Filter, filter);
            }

            return SetReply.Unsupported("only filter can be set on a wheel");
        }

        if (entity == b.FocuserEntity && b.FocuserEntity != null) {
            if (devices.Focuser is not { Connected: true } focuser) {
                return SetReply.Unbound("the focuser is not connected");
            }

            if (values.TryGetValue(Channels.Focus, out var f) && f.AsDouble() is { } position) {
                await focuser.MoveAsync((int)Math.Round(position), cancellationToken).ConfigureAwait(false);
                return SetReply.Done($"focus {focuser.Position}", Channels.Focus, focuser.Position ?? position);
            }

            return SetReply.Unsupported("only focus can be set on a focuser");
        }

        if (entity == b.RoofEntity && b.RoofEntity != null) {
            if (devices.Roof is not { Connected: true } roof) {
                return SetReply.Unbound("no roof is connected");
            }

            var joint = b.RoofJoints.FirstOrDefault(values.ContainsKey);

            if (joint == null) {
                return SetReply.Unsupported("set the roof's travel joint; `state` is observed");
            }

            var open = (values[joint].AsDouble() ?? 0) >= options.RoofOpenJointValue / 2;
            await (open ? roof.OpenAsync(cancellationToken) : roof.CloseAsync(cancellationToken)).ConfigureAwait(false);
            return SetReply.Done(open ? "roof open" : "roof closed", joint, open ? options.RoofOpenJointValue : 0.0);
        }

        return SetReply.Unbound("this agent drives no device behind that entity");
    }

    private Task<SetReply> SetSequencerAsync(IReadOnlyDictionary<string, Value> values, CancellationToken cancellationToken) {
        if (values.TryGetValue(Channels.SequencerTelemetryOnly, out var t) && t.AsBool() is { } telemetryOnly) {
            options.TelemetryOnly = telemetryOnly;
            Log(Level.Info, telemetryOnly ? "telemetry-only mode set by the service" : "full control enabled by the service");
            return Task.FromResult(SetReply.Done(telemetryOnly ? "telemetry only" : "full control", Channels.SequencerTelemetryOnly, telemetryOnly));
        }

        if (values.TryGetValue(Channels.SequencerState, out var s) && s.AsText() == Channels.SequencerStateIdle) {
            var running = RunningTasks;
            CancelRunningTasks();
            return Task.FromResult(SetReply.Done(running.Count == 0 ? "nothing was running" : $"stopping {running.Count} task(s)", Channels.SequencerState, Channels.SequencerStateIdle));
        }

        return Task.FromResult(SetReply.Unsupported("a sequencer takes telemetry_only and state = idle"));
    }

    private async Task<SetReply> SetMountAsync(string requestId, IReadOnlyDictionary<string, Value> values, CancellationToken cancellationToken) {
        var mount = devices.Mount;

        if (mount == null || !mount.Read().Connected) {
            return SetReply.Unbound("the mount is not connected");
        }

        if (RunningTasks.Count > 0 || instrument.CurrentCount == 0) {
            return SetReply.Busy("a task is running on this instrument");
        }

        var sky = values.ContainsKey(Channels.MountRaDeg) || values.ContainsKey(Channels.MountDecDeg);
        var axes = values.ContainsKey(Binding.HaJoint) || values.ContainsKey(Binding.DecJoint);

        if (sky && axes) {
            return SetReply.Rejected("set either the axes or ra_deg/dec_deg, not both");
        }

        if (values.TryGetValue(Channels.MountMode, out var modeValue)) {
            switch (modeValue.AsText()) {
                case Channels.MountModeParked:
                    await mount.ParkAsync(cancellationToken).ConfigureAwait(false);
                    CurrentTarget = null;
                    return SetReply.Done("parked", Channels.MountMode, Channels.MountModeParked);
                case Channels.MountModeStopped:
                    await mount.StopAsync(cancellationToken).ConfigureAwait(false);
                    CurrentTarget = null;
                    return SetReply.Done("stopped", Channels.MountMode, Channels.MountModeStopped);
                case Channels.MountModeTracking:
                    if (mount.Read().State == MountState.Parked) {
                        await mount.UnparkAsync(cancellationToken).ConfigureAwait(false);
                    }

                    await mount.SetTrackingAsync(true, cancellationToken).ConfigureAwait(false);
                    return SetReply.Done("tracking", Channels.MountMode, Channels.MountModeTracking);
                default:
                    return SetReply.Rejected($"mode `{modeValue}` cannot be asked for; set ra_deg/dec_deg to slew");
            }
        }

        if (sky) {
            var reading = mount.Read();
            var ra = values.TryGetValue(Channels.MountRaDeg, out var r) ? r.AsDouble() : reading.RightAscensionJ2000Deg;
            var dec = values.TryGetValue(Channels.MountDecDeg, out var d) ? d.AsDouble() : reading.DeclinationJ2000Deg;

            if (ra is not { } raDeg || dec is not { } decDeg) {
                return SetReply.Rejected("ra_deg and dec_deg must be numbers");
            }

            if (decDeg is < -90 or > 90) {
                return SetReply.Rejected("dec_deg must be within ±90");
            }

            // accepted now; done (or rejected) once the slew settles, from a background task
            _ = Task.Run(async () => {
                try {
                    if (mount.Read().State == MountState.Parked) {
                        await mount.UnparkAsync(cancellationToken).ConfigureAwait(false);
                    }

                    CurrentTarget = (raDeg, decDeg);
                    await mount.SlewAsync(raDeg, decDeg, cancellationToken).ConfigureAwait(false);

                    if (options.EnsureTrackingAfterSlew && mount.Read().State != MountState.Tracking) {
                        await mount.SetTrackingAsync(true, cancellationToken).ConfigureAwait(false);
                    }

                    await SetDoneAsync(requestId, "tracking", AxesObserved(mount.Read())).ConfigureAwait(false);
                } catch (Exception e) {
                    await SetFailedAsync(requestId, e.Message).ConfigureAwait(false);
                }
            }, CancellationToken.None);
            return SetReply.Accepted("slewing");
        }

        if (axes) {
            return SetReply.Unsupported("this agent slews by ra_deg/dec_deg, not by axis angles");
        }

        return SetReply.Unsupported("set mode or ra_deg/dec_deg");
    }

    /// <summary>What a mount settled on, as the properties the service graphs.</summary>
    private Dictionary<string, Value> AxesObserved(MountReading r) {
        var observed = new Dictionary<string, Value>();

        if (r.RightAscensionJ2000Deg is { } ra) {
            observed[Channels.MountRaDeg] = ra;
        }

        if (r.DeclinationJ2000Deg is { } dec) {
            observed[Channels.MountDecDeg] = dec;
        }

        observed[Channels.MountMode] = r.State switch {
            MountState.Parked => Channels.MountModeParked,
            MountState.Slewing => Channels.MountModeSlewing,
            MountState.Tracking => Channels.MountModeTracking,
            _ => Channels.MountModeStopped,
        };
        return observed;
    }

    // ---- commands ---------------------------------------------------------------------------------

    protected override async Task<string?> OnCommandAsync(Command command, CancellationToken cancellationToken) {
        if (options.TelemetryOnly) {
            throw new InvalidOperationException($"{command.Kind} refused: {TelemetryOnlyReason}");
        }

        var mount = devices.Mount;
        var roof = devices.Roof;

        switch (command.Kind) {
            case CommandKinds.Stop:
                CancelRunningTasks();

                if (mount is { }) {
                    await mount.StopAsync(cancellationToken).ConfigureAwait(false);
                }

                return "stopped";

            case CommandKinds.Park:
                CancelRunningTasks();
                await RequireMount().ParkAsync(cancellationToken).ConfigureAwait(false);
                CurrentTarget = null;
                return "parked";

            case CommandKinds.Unpark:
                await RequireMount().UnparkAsync(cancellationToken).ConfigureAwait(false);
                return "unparked";

            case CommandKinds.OpenRoof:
                await RequireRoof().OpenAsync(cancellationToken).ConfigureAwait(false);
                return "roof open";

            case CommandKinds.CloseRoof:
                await RequireRoof().CloseAsync(cancellationToken).ConfigureAwait(false);
                return "roof closed";

            case CommandKinds.Safe:
                CancelRunningTasks();

                if (mount is { } m && m.Read().Connected) {
                    await m.StopAsync(cancellationToken).ConfigureAwait(false);
                    await m.ParkAsync(cancellationToken).ConfigureAwait(false);
                    CurrentTarget = null;
                }

                if (options.CloseRoofOnSafe && roof is { Connected: true }) {
                    await roof.CloseAsync(cancellationToken).ConfigureAwait(false);
                }

                return "safe";

            default:
                throw new NotSupportedException(command.Kind);
        }

        IMountDevice RequireMount() => mount is { } d && d.Read().Connected ? d : throw new InvalidOperationException("mount is not connected");
        IRoofDevice RequireRoof() => roof is { Connected: true } d ? d : throw new InvalidOperationException("no roof is connected");
    }
}

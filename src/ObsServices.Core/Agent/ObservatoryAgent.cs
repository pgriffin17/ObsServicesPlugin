using ObsServices.Core.Devices;
using ObsServices.Core.Protocol;

namespace ObsServices.Core.Agent;

public sealed class ObservatoryAgentOptions {
    /// <summary>Instrument to drive: id, name or mount instance name. Null means the site's first.</summary>
    public string? InstrumentKey { get; set; }

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
    }

    /// <summary>How the manifest was bound to the devices; empty until <c>welcome</c>.</summary>
    public SiteBinding Binding { get; private set; } = SiteBinding.Empty;

    /// <summary>The J2000 target the mount was last asked to point at, for the aim reticle.</summary>
    public (double Ra, double Dec)? CurrentTarget {
        get { lock (gate) return target; }
        private set { lock (gate) target = value; }
    }

    protected override Task OnManifestAsync(Manifest manifest, CancellationToken cancellationToken) {
        Binding = SiteBinding.FromManifest(manifest, options.InstrumentKey);

        if (Binding.MountEntity == null) {
            Log(Level.Warn, options.InstrumentKey == null ? "the site has no instrument; nothing to drive" : $"instrument `{options.InstrumentKey}` is not in the site");
        }

        return Task.CompletedTask;
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
            m.Set(entity, b.HaJoint, Astro.HourAngleDeg(lst, ra));
            m.Set(entity, b.DecJoint, dec);
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
                await SelectFilterAsync(handle, frame.Filter, ct).ConfigureAwait(false);
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

    private async Task SelectFilterAsync(TaskHandle handle, string filter, CancellationToken ct) {
        var wheel = devices.FilterWheel;

        if (wheel is not { Connected: true }) {
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

    // ---- commands ---------------------------------------------------------------------------------

    protected override async Task<string?> OnCommandAsync(Command command, CancellationToken cancellationToken) {
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

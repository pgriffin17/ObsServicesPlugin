using System.IO;
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.Utility;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Plugin;
using NINA.Plugin.Interfaces;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;
using ObsServices.Core.Agent;
using ObsServices.Core.Devices;
using ObsServices.Core.Imaging;
using ObsServices.Core.Protocol;
using ObsServicesPlugin.Nina;

namespace ObsServicesPlugin;

/// <summary>
/// The plugin manifest and the host of one site agent. N.I.N.A. constructs it through MEF with the
/// mediators it needs, calls <see cref="Initialize"/> after all plugins are loaded and
/// <see cref="Teardown"/> on shutdown. The options page binds to this object.
/// </summary>
[Export(typeof(IPluginManifest))]
public sealed class Plugin : PluginBase, INotifyPropertyChanged {
    private readonly IProfileService profiles;
    private readonly IDeviceSet devices;
    private readonly NinaSequencerHost? sequencerHost;
    private readonly NinaFrameSource? frameSource;
    private readonly object gate = new();
    private NinaSiteAgent? agent;
    private FrameSpool? spool;
    private ObservatoryAgentOptions? agentOptions;
    private Task? runner;
    private CancellationTokenSource? runCts;
    private string status = "Disconnected";
    private string lastMessage = string.Empty;

    [ImportingConstructor]
    public Plugin(IProfileService profiles, ITelescopeMediator telescope, ICameraMediator camera, IImagingMediator imaging, IImageSaveMediator imageSave, IFilterWheelMediator filterWheel, IDomeMediator dome, IFocuserMediator focuser, ISequenceMediator sequence)
        : this(profiles, Compose(profiles, telescope, camera, imaging, imageSave, filterWheel, dome, focuser, sequence), imageSave) {
    }

    private static DeviceSet Compose(IProfileService profiles, ITelescopeMediator telescope, ICameraMediator camera, IImagingMediator imaging, IImageSaveMediator imageSave, IFilterWheelMediator filterWheel, IDomeMediator dome, IFocuserMediator focuser, ISequenceMediator sequence) =>
        new() {
            Mount = new NinaMount(telescope),
            Camera = new NinaCamera(camera, imaging, imageSave, profiles),
            FilterWheel = new NinaFilterWheel(filterWheel, profiles),
            Roof = new NinaRoof(dome),
            Focuser = new NinaFocuser(focuser),
            Sequencer = new NinaSequencerHost(sequence, imageSave, profiles),
        };

    /// <summary>For tests and other hosts: any device set; frames are shared only when an image save mediator is given.</summary>
    public Plugin(IProfileService profiles, IDeviceSet devices, IImageSaveMediator? imageSave = null) {
        this.profiles = profiles;
        this.devices = devices;
        sequencerHost = devices.Sequencer as NinaSequencerHost;
        Settings = new Settings(profiles, Guid.Parse(Identifier));

        if (imageSave != null) {
            frameSource = new NinaFrameSource(imageSave, () => agent, () => new FrameSharing(Settings.ShareFrames, Settings.ShareCalibrationFrames, Settings.ShareSnapshots));
            frameSource.Changed += () => Raise(nameof(SpoolStatus));
        }

        ConnectCommand = new AsyncRelayCommand(ConnectAsync, () => !IsConnected);
        DisconnectCommand = new AsyncRelayCommand(DisconnectAsync, () => IsConnected);
        profiles.ProfileChanged += OnProfileChanged;
    }

    public Settings Settings { get; private set; }

    public ICommand ConnectCommand { get; }

    public ICommand DisconnectCommand { get; }

    /// <summary>True while an agent is running (connected or retrying).</summary>
    public bool IsConnected => runner is { IsCompleted: false };

    public string Status {
        get => status;
        private set => Set(ref status, value);
    }

    public string LastMessage {
        get => lastMessage;
        private set => Set(ref lastMessage, value);
    }

    // ---- settings surfaced for binding (the page edits this object) --------------------------------

    public string ServiceUrl {
        get => Settings.ServiceUrl;
        set { Settings.ServiceUrl = value; Raise(); }
    }

    public string SiteId {
        get => Settings.SiteId;
        set { Settings.SiteId = value; Raise(); }
    }

    public string AgentToken {
        get => Settings.AgentToken;
        set { Settings.AgentToken = value; Raise(); }
    }

    public string Instrument {
        get => Settings.Instrument;
        set { Settings.Instrument = value; Raise(); }
    }

    public bool AutoConnect {
        get => Settings.AutoConnect;
        set { Settings.AutoConnect = value; Raise(); }
    }

    /// <summary>Takes effect immediately, also on a running agent.</summary>
    public bool TelemetryOnly {
        get => Settings.TelemetryOnly;
        set {
            Settings.TelemetryOnly = value;

            lock (gate) {
                if (agentOptions != null) {
                    agentOptions.TelemetryOnly = value;
                }
            }

            Raise();
            Raise(nameof(ModeDescription));
        }
    }

    public string ModeDescription => TelemetryOnly
        ? "Telemetry only: the service sees this rig but cannot move it. Tasks are skipped, commands refused."
        : "Full control: the service may assign observations and send stop, park, unpark, roof and safe commands.";

    public double StateIntervalSeconds {
        get => Settings.StateIntervalSeconds;
        set { Settings.StateIntervalSeconds = value; Raise(); }
    }

    public double RoofOpenJointValue {
        get => Settings.RoofOpenJointValue;
        set { Settings.RoofOpenJointValue = value; Raise(); }
    }

    public string Sequencer {
        get => Settings.Sequencer;
        set { Settings.Sequencer = value; Raise(); }
    }

    /// <summary>Takes effect immediately.</summary>
    public bool AllowWholeSequences {
        get => Settings.AllowWholeSequences;
        set {
            Settings.AllowWholeSequences = value;

            if (sequencerHost != null) {
                sequencerHost.AllowWholeSequences = value;
            }

            Raise();
        }
    }

    /// <summary>The program formats this N.I.N.A. announces to the service.</summary>
    public string ProgramFormats => devices.Sequencer == null ? "none" : string.Join(", ", devices.Sequencer.Formats);

    // ---- frames ----------------------------------------------------------------------------------

    /// <summary>Takes effect at the next connect (the capability is declared in hello); the kind switches below are live.</summary>
    public bool ShareFrames {
        get => Settings.ShareFrames;
        set { Settings.ShareFrames = value; Raise(); Raise(nameof(SpoolStatus)); }
    }

    public bool ShareCalibrationFrames {
        get => Settings.ShareCalibrationFrames;
        set { Settings.ShareCalibrationFrames = value; Raise(); }
    }

    public bool ShareSnapshots {
        get => Settings.ShareSnapshots;
        set { Settings.ShareSnapshots = value; Raise(); }
    }

    public string SpoolDirectory {
        get => Settings.SpoolDirectory;
        set { Settings.SpoolDirectory = value; Raise(); }
    }

    /// <summary>Takes effect immediately on the running spool.</summary>
    public double SpoolCapGb {
        get => Settings.SpoolCapGb;
        set {
            Settings.SpoolCapGb = value;

            lock (gate) {
                if (spool != null) {
                    spool.MaxBytes = CapBytes(value);
                    spool.Trim();
                }
            }

            Raise();
        }
    }

    /// <summary>Takes effect immediately on the running spool.</summary>
    public bool DeleteOnRelease {
        get => Settings.DeleteOnRelease;
        set {
            Settings.DeleteOnRelease = value;

            lock (gate) {
                if (spool != null) {
                    spool.DeleteOnRelease = value;
                }
            }

            Raise();
        }
    }

    /// <summary>What the spool is doing, for the options page.</summary>
    public string SpoolStatus {
        get {
            FrameSpool? s;

            lock (gate) {
                s = spool;
            }

            if (frameSource == null) {
                return "Frames are not available in this host.";
            }

            if (!Settings.ShareFrames) {
                return "Frames stay on this machine.";
            }

            if (s == null) {
                return IsConnected ? "Sharing starts at the next connect." : "Frames will be shared once connected.";
            }

            var pending = s.Pending;
            var waiting = pending.Count == 0 ? "nothing waiting" : $"{pending.Count} frame(s), {pending.Sum(e => e.Bytes) / 1048576.0:0.#} MB waiting";
            var queue = frameSource.Queued > 0 ? $", {frameSource.Queued} being prepared" : string.Empty;
            var error = frameSource.LastError == null ? string.Empty : $" — last error: {frameSource.LastError}";
            return $"{frameSource.Announced} announced, {waiting}{queue}, spool at {s.Directory}{error}";
        }
    }

    /// <summary>The spool directory in use: the setting, or N.I.N.A.'s local app data.</summary>
    public static string ResolveSpoolDirectory(string setting) =>
        string.IsNullOrWhiteSpace(setting) ? Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "ObsServices", "spool") : Environment.ExpandEnvironmentVariables(setting);

    private static long? CapBytes(double gb) => gb <= 0 ? null : (long)(gb * (1L << 30));

    // ---- lifecycle ---------------------------------------------------------------------------------

    public override Task Initialize() {
        Logger.Info($"[ObsServices] plugin {Version} loaded");

        if (Settings.AutoConnect && Settings.IsComplete) {
            return ConnectAsync();
        }

        return Task.CompletedTask;
    }

    public override async Task Teardown() {
        profiles.ProfileChanged -= OnProfileChanged;
        frameSource?.Dispose();
        await DisconnectAsync();
    }

    public Task ConnectAsync() {
        if (!Settings.IsComplete) {
            Status = "Not configured";
            LastMessage = "Set the service URL, site id and agent token first.";
            return Task.CompletedTask;
        }

        lock (gate) {
            if (IsConnected) {
                return Task.CompletedTask;
            }

            var options = new ObservatoryAgentOptions {
                InstrumentKey = string.IsNullOrWhiteSpace(Settings.Instrument) ? null : Settings.Instrument,
                SequencerKey = string.IsNullOrWhiteSpace(Settings.Sequencer) ? null : Settings.Sequencer,
                RoofOpenJointValue = Settings.RoofOpenJointValue,
                TelemetryOnly = Settings.TelemetryOnly,
            };

            if (sequencerHost != null) {
                sequencerHost.AllowWholeSequences = Settings.AllowWholeSequences;
            }
            var a = new NinaSiteAgent(Settings.ServiceUrl, Settings.SiteId, Settings.AgentToken, devices, options, Version?.ToString() ?? "0.0.0.0") {
                StateInterval = TimeSpan.FromSeconds(Settings.StateIntervalSeconds),
            };
            a.Logged += (level, message) => LastMessage = $"{level}: {message}";

            if (frameSource != null && Settings.ShareFrames) {
                try {
                    var s = new FrameSpool(ResolveSpoolDirectory(Settings.SpoolDirectory)) { MaxBytes = CapBytes(Settings.SpoolCapGb), DeleteOnRelease = Settings.DeleteOnRelease };
                    s.Changed += () => Raise(nameof(SpoolStatus));
                    a.Spool = s;
                    spool = s;
                } catch (Exception e) {
                    Logger.Error($"[ObsServices] cannot open the frame spool; frames are not shared: {e.Message}");
                    LastMessage = $"spool: {e.Message}";
                }
            }

            a.TelemetryOnlyChanged += on => {
                Settings.TelemetryOnly = on;
                Raise(nameof(TelemetryOnly));
                Raise(nameof(ModeDescription));
            };
            runCts = new CancellationTokenSource();
            agentOptions = options;
            agent = a;
            runner = Task.Run(() => RunAgentAsync(a, runCts.Token));
        }

        Status = $"Connecting to {Settings.SiteId}";
        RaiseConnectionChanged();
        return Task.CompletedTask;
    }

    public async Task DisconnectAsync() {
        Task? running;

        lock (gate) {
            running = runner;
            agent?.Stop();
            runCts?.Cancel();
        }

        if (running != null) {
            try {
                await running.WaitAsync(TimeSpan.FromSeconds(10));
            } catch (Exception e) {
                Logger.Warning($"[ObsServices] agent did not stop cleanly: {e.Message}");
            }
        }

        lock (gate) {
            runner = null;
            agent = null;
            agentOptions = null;
            spool = null;
            runCts?.Dispose();
            runCts = null;
        }

        Status = "Disconnected";
        RaiseConnectionChanged();
        Raise(nameof(SpoolStatus));
    }

    private async Task RunAgentAsync(NinaSiteAgent a, CancellationToken ct) {
        var watcher = WatchConnectionAsync(a, ct);

        try {
            await a.RunAsync(ct);
        } catch (Exception e) {
            Logger.Error("[ObsServices] agent stopped unexpectedly", e);
            LastMessage = $"error: {e.Message}";
        } finally {
            try {
                await watcher;
            } catch (OperationCanceledException) {
            }
        }
    }

    private async Task WatchConnectionAsync(NinaSiteAgent a, CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            await a.Connected.WaitAsync(ct);
            Status = $"Connected to {a.SiteId}";
            await a.Ready.WaitAsync(ct);
            Status = a.Binding.MountEntity == null ? $"Connected to {a.SiteId} (no instrument bound)" : $"Online: {a.SiteId}, instrument {a.Binding.InstrumentId}";
            Raise(nameof(SpoolStatus));

            // wait for the connection to drop, then report and loop
            while (a.IsConnected && !ct.IsCancellationRequested) {
                await Task.Delay(500, ct);
            }

            Status = $"Reconnecting to {a.SiteId}";
        }
    }

    private void OnProfileChanged(object? sender, EventArgs e) {
        // settings live in the profile: the page must re-read, and a running agent belongs to the old profile
        Settings = new Settings(profiles, Guid.Parse(Identifier));
        Raise(nameof(Settings));

        foreach (var name in new[] { nameof(ServiceUrl), nameof(SiteId), nameof(AgentToken), nameof(Instrument), nameof(Sequencer), nameof(AllowWholeSequences), nameof(AutoConnect), nameof(TelemetryOnly), nameof(ModeDescription), nameof(StateIntervalSeconds), nameof(RoofOpenJointValue), nameof(ShareFrames), nameof(ShareCalibrationFrames), nameof(ShareSnapshots), nameof(SpoolDirectory), nameof(SpoolCapGb), nameof(DeleteOnRelease), nameof(SpoolStatus) }) {
            Raise(name);
        }

        if (IsConnected) {
            _ = DisconnectAsync().ContinueWith(_ => {
                if (Settings.AutoConnect && Settings.IsComplete) {
                    return ConnectAsync();
                }

                return Task.CompletedTask;
            }, TaskScheduler.Default).Unwrap();
        }
    }

    // ---- INotifyPropertyChanged --------------------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaiseConnectionChanged() {
        Raise(nameof(IsConnected));
        ((IRelayCommand)ConnectCommand).NotifyCanExecuteChanged();
        ((IRelayCommand)DisconnectCommand).NotifyCanExecuteChanged();
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) {
        if (!EqualityComparer<T>.Default.Equals(field, value)) {
            field = value;
            Raise(name);
        }
    }
}

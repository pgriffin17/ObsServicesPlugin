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
using NINA.WPF.Base.Interfaces.Mediator;
using ObsServices.Core.Agent;
using ObsServices.Core.Devices;
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
    private readonly object gate = new();
    private NinaSiteAgent? agent;
    private ObservatoryAgentOptions? agentOptions;
    private Task? runner;
    private CancellationTokenSource? runCts;
    private string status = "Disconnected";
    private string lastMessage = string.Empty;

    [ImportingConstructor]
    public Plugin(IProfileService profiles, ITelescopeMediator telescope, ICameraMediator camera, IImagingMediator imaging, IImageSaveMediator imageSave, IFilterWheelMediator filterWheel, IDomeMediator dome, IFocuserMediator focuser)
        : this(profiles, new DeviceSet {
            Mount = new NinaMount(telescope),
            Camera = new NinaCamera(camera, imaging, imageSave, profiles),
            FilterWheel = new NinaFilterWheel(filterWheel, profiles),
            Roof = new NinaRoof(dome),
            Focuser = new NinaFocuser(focuser),
        }) {
    }

    /// <summary>For tests and other hosts: any device set.</summary>
    public Plugin(IProfileService profiles, IDeviceSet devices) {
        this.profiles = profiles;
        this.devices = devices;
        Settings = new Settings(profiles, Guid.Parse(Identifier));
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
                RoofOpenJointValue = Settings.RoofOpenJointValue,
                TelemetryOnly = Settings.TelemetryOnly,
            };
            var a = new NinaSiteAgent(Settings.ServiceUrl, Settings.SiteId, Settings.AgentToken, devices, options, Version?.ToString() ?? "0.0.0.0") {
                StateInterval = TimeSpan.FromSeconds(Settings.StateIntervalSeconds),
            };
            a.Logged += (level, message) => LastMessage = $"{level}: {message}";
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
            runCts?.Dispose();
            runCts = null;
        }

        Status = "Disconnected";
        RaiseConnectionChanged();
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

        foreach (var name in new[] { nameof(ServiceUrl), nameof(SiteId), nameof(AgentToken), nameof(Instrument), nameof(AutoConnect), nameof(TelemetryOnly), nameof(ModeDescription), nameof(StateIntervalSeconds), nameof(RoofOpenJointValue) }) {
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

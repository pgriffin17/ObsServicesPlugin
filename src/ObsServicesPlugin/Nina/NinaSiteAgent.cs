using NINA.Core.Utility;
using ObsServices.Core.Agent;
using ObsServices.Core.Devices;
using ObsServices.Core.Protocol;

namespace ObsServicesPlugin.Nina;

/// <summary>The generic agent with its diagnostics routed into N.I.N.A.'s log.</summary>
public sealed class NinaSiteAgent : ObservatoryAgent {
    public NinaSiteAgent(string baseUrl, string siteId, string token, IDeviceSet devices, ObservatoryAgentOptions options, string version)
        : base(baseUrl, siteId, token, devices, options) {
        Name = "nina-obsservices";
        Version = version;
    }

    /// <summary>Raised on every log line so the options page can show the latest.</summary>
    public event Action<Level, string>? Logged;

    protected override void Log(Level level, string message, Exception? exception = null) {
        switch (level) {
            case Level.Error:
                Logger.Error($"[ObsServices] {message}", exception);
                break;
            case Level.Warn:
                Logger.Warning($"[ObsServices] {message}");
                break;
            default:
                Logger.Info($"[ObsServices] {message}");
                break;
        }

        Logged?.Invoke(level, message);
    }
}

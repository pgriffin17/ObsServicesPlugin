using NINA.Profile;
using NINA.Profile.Interfaces;

namespace ObsServicesPlugin;

/// <summary>
/// What the plugin needs to join a site, stored per N.I.N.A. profile through the plugin settings
/// store (so switching profiles switches sites). Keys are stable: changing them orphans saved values.
/// </summary>
public sealed class Settings {
    private readonly IPluginOptionsAccessor options;

    public const string DefaultServiceUrl = "https://observatory.services";

    public Settings(IProfileService profiles, Guid pluginId) : this(new PluginOptionsAccessor(profiles, pluginId)) {
    }

    public Settings(IPluginOptionsAccessor options) {
        this.options = options;
    }

    public string ServiceUrl {
        get => options.GetValueString(nameof(ServiceUrl), DefaultServiceUrl);
        set => options.SetValueString(nameof(ServiceUrl), (value ?? string.Empty).Trim());
    }

    public string SiteId {
        get => options.GetValueString(nameof(SiteId), string.Empty);
        set => options.SetValueString(nameof(SiteId), (value ?? string.Empty).Trim());
    }

    /// <summary>The <c>tca_…</c> token minted in the site's Agent tokens window.</summary>
    public string AgentToken {
        get => options.GetValueString(nameof(AgentToken), string.Empty);
        set => options.SetValueString(nameof(AgentToken), (value ?? string.Empty).Trim());
    }

    /// <summary>Instrument to drive when the site has several (id, name or mount instance name). Empty = the first.</summary>
    public string Instrument {
        get => options.GetValueString(nameof(Instrument), string.Empty);
        set => options.SetValueString(nameof(Instrument), (value ?? string.Empty).Trim());
    }

    /// <summary>Connect to the service as soon as N.I.N.A. has loaded the plugin.</summary>
    public bool AutoConnect {
        get => options.GetValueBoolean(nameof(AutoConnect), false);
        set => options.SetValueBoolean(nameof(AutoConnect), value);
    }

    public double StateIntervalSeconds {
        get => options.GetValueDouble(nameof(StateIntervalSeconds), 1.0);
        set => options.SetValueDouble(nameof(StateIntervalSeconds), Math.Clamp(value, 0.2, 60));
    }

    /// <summary>Joint value reported for the roof when open, millimetres (the twin's roof travel).</summary>
    public double RoofOpenJointValue {
        get => options.GetValueDouble(nameof(RoofOpenJointValue), 2400);
        set => options.SetValueDouble(nameof(RoofOpenJointValue), value);
    }

    public bool IsComplete => !string.IsNullOrWhiteSpace(ServiceUrl) && !string.IsNullOrWhiteSpace(SiteId) && !string.IsNullOrWhiteSpace(AgentToken);
}

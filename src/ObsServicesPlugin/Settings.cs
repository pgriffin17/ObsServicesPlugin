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

    /// <summary>
    /// Publish telemetry but accept no tasks or commands from the service. On by default: a fresh
    /// install never hands control of the rig to anyone until this is switched off.
    /// </summary>
    public bool TelemetryOnly {
        get => options.GetValueBoolean(nameof(TelemetryOnly), true);
        set => options.SetValueBoolean(nameof(TelemetryOnly), value);
    }

    /// <summary>The site's <c>sequencer</c> instance this N.I.N.A. publishes itself to, when the site has several. Empty = the first.</summary>
    public string Sequencer {
        get => options.GetValueString(nameof(Sequencer), string.Empty);
        set => options.SetValueString(nameof(Sequencer), (value ?? string.Empty).Trim());
    }

    /// <summary>Let a <c>nina.sequence</c> program replace the loaded sequence and run it. Off by default; target and template programs are always allowed.</summary>
    public bool AllowWholeSequences {
        get => options.GetValueBoolean(nameof(AllowWholeSequences), false);
        set => options.SetValueBoolean(nameof(AllowWholeSequences), value);
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

    // ---- frames ------------------------------------------------------------------------------------

    /// <summary>
    /// Announce the frames N.I.N.A. saves and upload them when the service asks. Off by default: a granted
    /// upload moves the whole frame off this machine, which is the user's call. Takes effect at the next connect.
    /// </summary>
    public bool ShareFrames {
        get => options.GetValueBoolean(nameof(ShareFrames), false);
        set => options.SetValueBoolean(nameof(ShareFrames), value);
    }

    /// <summary>Also share bias, dark and flat frames (lights are always shared when sharing is on).</summary>
    public bool ShareCalibrationFrames {
        get => options.GetValueBoolean(nameof(ShareCalibrationFrames), false);
        set => options.SetValueBoolean(nameof(ShareCalibrationFrames), value);
    }

    /// <summary>Also share snapshots from the imaging tab (framing and focus test shots).</summary>
    public bool ShareSnapshots {
        get => options.GetValueBoolean(nameof(ShareSnapshots), false);
        set => options.SetValueBoolean(nameof(ShareSnapshots), value);
    }

    /// <summary>Where the plain FITS copies wait for their upload. Empty = <c>%LOCALAPPDATA%\NINA\ObsServices\spool</c>.</summary>
    public string SpoolDirectory {
        get => options.GetValueString(nameof(SpoolDirectory), string.Empty);
        set => options.SetValueString(nameof(SpoolDirectory), (value ?? string.Empty).Trim());
    }

    /// <summary>Cap on the spool's copies, gigabytes; frames the service already received go first when over it. 0 = no cap.</summary>
    public double SpoolCapGb {
        get => options.GetValueDouble(nameof(SpoolCapGb), 20);
        set => options.SetValueDouble(nameof(SpoolCapGb), Math.Max(0, value));
    }

    /// <summary>Delete the spool's copy once the service holds the frame. The user's own file is never touched.</summary>
    public bool DeleteOnRelease {
        get => options.GetValueBoolean(nameof(DeleteOnRelease), true);
        set => options.SetValueBoolean(nameof(DeleteOnRelease), value);
    }

    // ---- updates -----------------------------------------------------------------------------------

    public const string DefaultRepositoryUrl = "https://github.com/pgriffin17/ObsServicesPlugin.git";

    /// <summary>The git repository the in-app updater builds from.</summary>
    public string UpdateRepositoryUrl {
        get => options.GetValueString(nameof(UpdateRepositoryUrl), DefaultRepositoryUrl);
        set => options.SetValueString(nameof(UpdateRepositoryUrl), string.IsNullOrWhiteSpace(value) ? DefaultRepositoryUrl : value.Trim());
    }

    /// <summary>The branch to follow; <c>dev</c> by default, <c>nina-3.2</c> on a 3.2 install.</summary>
    public string UpdateBranch {
        get => options.GetValueString(nameof(UpdateBranch), "dev");
        set => options.SetValueString(nameof(UpdateBranch), string.IsNullOrWhiteSpace(value) ? "dev" : value.Trim());
    }

    /// <summary>
    /// A checkout of your own to build from instead of the plugin's clone. It is only ever fast-forwarded and is left
    /// alone when it has uncommitted changes. Empty = the plugin's clone under <c>%LOCALAPPDATA%\NINA\ObsServices\src</c>.
    /// </summary>
    public string UpdateSourceDirectory {
        get => options.GetValueString(nameof(UpdateSourceDirectory), string.Empty);
        set => options.SetValueString(nameof(UpdateSourceDirectory), (value ?? string.Empty).Trim());
    }

    /// <summary>Fetch the branch when connecting and show how far behind this build is. Never builds on its own.</summary>
    public bool CheckUpdatesOnConnect {
        get => options.GetValueBoolean(nameof(CheckUpdatesOnConnect), false);
        set => options.SetValueBoolean(nameof(CheckUpdatesOnConnect), value);
    }

    public bool IsComplete => !string.IsNullOrWhiteSpace(ServiceUrl) && !string.IsNullOrWhiteSpace(SiteId) && !string.IsNullOrWhiteSpace(AgentToken);
}

using System.Security.Cryptography;
using System.Text;

namespace ObsServices.Core;

/// <summary>
/// Property names shared with the service, and the addressing helpers. Telemetry is addressed to a
/// property of an entity; the flat form <c>"{entityId}.{property}"</c> is what graphs, history
/// queries and CSV exports use. The manifest an agent receives in <c>welcome</c> is the source of
/// truth for a given site; these are the names the service's virtual agent publishes and the UI reads.
/// Units are degrees, millimetres and seconds.
/// </summary>
public static class Channels {
    // ---- mounts (plus the joint properties: ra/dec or azimuth/elevation) -----------------------

    /// <summary><c>parked</c> | <c>slewing</c> | <c>tracking</c> | <c>stopped</c>.</summary>
    public const string MountMode = "mode";

    /// <summary>Sky right ascension the mount is on, degrees. Informational: the <c>ra</c> joint is the hour angle.</summary>
    public const string MountRaDeg = "ra_deg";

    /// <summary>Sky declination the mount is on, degrees. The <c>dec</c> joint is the axis angle, which is past ±90° on a flipped German equatorial.</summary>
    public const string MountDecDeg = "dec_deg";

    /// <summary>German equatorials: <c>east</c> (normal pose, dec axis within ±90°) | <c>west</c> (flipped, both axes through 180°).</summary>
    public const string MountPierSide = "pier_side";

    public const string PierEast = "east";
    public const string PierWest = "west";

    /// <summary>Where the mount is aimed, altitude (used for the reticle when no optics are declared).</summary>
    public const string MountTargetAltDeg = "target_alt_deg";

    public const string MountTargetAzDeg = "target_az_deg";

    public const string MountModeParked = "parked";
    public const string MountModeSlewing = "slewing";
    public const string MountModeTracking = "tracking";
    public const string MountModeStopped = "stopped";

    // ---- cameras -------------------------------------------------------------------------------

    /// <summary><c>idle</c> | <c>exposing &lt;filter&gt;</c> | <c>reading out</c>.</summary>
    public const string CameraState = "state";

    /// <summary>Length of the exposure in progress, seconds (0 when idle).</summary>
    public const string CameraExposureS = "exposure_s";

    public const string CameraExposureRemainingS = "exposure_remaining_s";

    public const string CameraTemperatureC = "temperature_c";

    public const string CameraStateIdle = "idle";
    public const string CameraStateReadingOut = "reading out";

    public static string CameraStateExposing(string filter) => $"exposing {filter}";

    // ---- roofs, filter wheels, focusers --------------------------------------------------------

    /// <summary><c>closed</c> | <c>opening</c> | <c>open</c> | <c>closing</c> (next to the <c>roof*</c> joint properties).</summary>
    public const string RoofState = "state";

    public const string RoofClosed = "closed";
    public const string RoofOpening = "opening";
    public const string RoofOpen = "open";
    public const string RoofClosing = "closing";

    /// <summary>Filter wheels: the filter in the light path.</summary>
    public const string Filter = "filter";

    /// <summary>Focusers: the joint property of the same name.</summary>
    public const string Focus = "focus";

    // ---- tasks (every task is an entity whose id is the task id) -------------------------------

    /// <summary>Writing it moves the task record: see <see cref="Protocol.ObservationStatus"/>.</summary>
    public const string TaskStatus = "status";

    public const string TaskProgress = "progress";

    public const string TaskMessage = "message";

    public const string TaskFramesDone = "frames_done";

    public const string TaskFramesTotal = "frames_total";

    // ---- the site and the agent (built-in entities) ---------------------------------------------

    public const string SiteLstDeg = "lst_deg";

    public const string AgentTimeScale = "time_scale";

    /// <summary>Simulated agents only: their clock, which the site view follows.</summary>
    public const string AgentSimTime = "sim_time";

    // ---- addressing -----------------------------------------------------------------------------

    /// <summary><c>Channel("6f1c…", "ra") == "6f1c….ra"</c>: the flat address of a property.</summary>
    public static string Channel(string entityId, string property) => $"{entityId}.{property}";

    /// <summary>Inverse of <see cref="Channel"/>. A channel without a dot is all property.</summary>
    public static (string Entity, string Property) SplitChannel(string channel) {
        var i = channel.LastIndexOf('.');
        return i < 0 ? (string.Empty, channel) : (channel[..i], channel[(i + 1)..]);
    }

    /// <summary>
    /// A stable entity id from a site id and a name, the same way the service derives the ids of a
    /// site's own entity (<c>"site"</c>), its agent (<c>"agent"</c>) and the virtual agent's devices
    /// (<c>"device:&lt;name&gt;"</c>). Use it for devices your agent declares so they keep their
    /// identity across restarts without storing anything.
    /// </summary>
    public static string DerivedEntityId(string siteId, string name) {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"telescope-entity:{siteId}:{name}"));
        return UuidFrom(digest, 5);
    }

    public static string SiteEntityId(string siteId) => DerivedEntityId(siteId, "site");

    public static string AgentEntityId(string siteId) => DerivedEntityId(siteId, "agent");

    public static string DeviceEntityId(string siteId, string deviceName) => DerivedEntityId(siteId, $"device:{deviceName}");

    private static string UuidFrom(ReadOnlySpan<byte> digest, int version) {
        Span<byte> b = stackalloc byte[16];
        digest[..16].CopyTo(b);
        b[6] = (byte)((b[6] & 0x0F) | (version << 4));
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        var h = Convert.ToHexStringLower(b);
        return $"{h[0..8]}-{h[8..12]}-{h[12..16]}-{h[16..20]}-{h[20..32]}";
    }
}

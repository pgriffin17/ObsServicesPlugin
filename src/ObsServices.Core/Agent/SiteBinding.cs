using ObsServices.Core.Protocol;

namespace ObsServices.Core.Agent;

/// <summary>
/// Which manifest entities the host's devices stand for. Derived from the manifest: the chosen
/// instrument's mount and camera, the filter wheel and focuser mated under that mount, and the first
/// component with roof joints. Everything is optional; unbound devices are simply not published.
/// </summary>
public sealed class SiteBinding {
    public string? InstrumentId { get; init; }

    public string? MountEntity { get; init; }

    /// <summary>Joint names the mount publishes: hour angle and declination for an equatorial mount.</summary>
    public string HaJoint { get; init; } = "ra";

    public string DecJoint { get; init; } = "dec";

    public bool IsAltAz { get; init; }

    public string? CameraEntity { get; init; }

    public string? FilterWheelEntity { get; init; }

    public string? FocuserEntity { get; init; }

    public string? RoofEntity { get; init; }

    public IReadOnlyList<string> RoofJoints { get; init; } = Array.Empty<string>();

    public static readonly SiteBinding Empty = new();

    /// <summary>
    /// Bind to the instrument named by <paramref name="instrumentKey"/> (id, name or mount instance
    /// name), or the site's first instrument when null.
    /// </summary>
    public static SiteBinding FromManifest(Manifest manifest, string? instrumentKey = null) {
        var instrument = instrumentKey == null ? manifest.Instruments.FirstOrDefault() : manifest.Instrument(instrumentKey);
        var roof = manifest.OfKind(EntityKind.Component).Select(e => (Entity: e, Joints: e.Joints().Where(j => j.StartsWith("roof", StringComparison.Ordinal)).ToList())).FirstOrDefault(r => r.Joints.Count > 0);

        if (instrument == null) {
            return new SiteBinding { RoofEntity = roof.Entity?.Id, RoofJoints = roof.Joints ?? new List<string>() };
        }

        var mount = manifest.Entity(instrument.Mount);
        var joints = mount?.Joints() ?? Array.Empty<string>();
        var ha = joints.FirstOrDefault(j => j is "ra" or "ha");
        var az = joints.FirstOrDefault(j => j is "azimuth" or "az");
        var alt = joints.FirstOrDefault(j => j is "elevation" or "alt");
        var altAz = ha == null && az != null && alt != null;
        var below = manifest.Under(instrument.Mount);

        return new SiteBinding {
            InstrumentId = instrument.Id,
            MountEntity = instrument.Mount,
            IsAltAz = altAz,
            HaJoint = altAz ? az! : ha ?? "ra",
            DecJoint = altAz ? alt! : joints.FirstOrDefault(j => j == "dec") ?? "dec",
            CameraEntity = instrument.Camera,
            FilterWheelEntity = below.FirstOrDefault(e => e.Property(Channels.Filter) != null)?.Id,
            FocuserEntity = below.FirstOrDefault(e => e.Joints().Contains(Channels.Focus))?.Id,
            RoofEntity = roof.Entity?.Id,
            RoofJoints = roof.Joints ?? new List<string>(),
        };
    }
}

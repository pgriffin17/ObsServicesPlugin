using System.Text.Json.Serialization;

namespace ObsServices.Core.Protocol;

/// <summary>
/// Where to point: J2000 coordinates and, for a target that moves, a <see cref="Body"/> saying how (the
/// coordinates are then its reference position).
/// </summary>
public sealed class Target {
    public string Name { get; set; } = string.Empty;

    public double RaDeg { get; set; }

    public double DecDeg { get; set; }

    public Body? Body { get; set; }

    /// <summary>The capability an agent needs to take this target as given (<c>target:&lt;kind&gt;</c>), if it moves.</summary>
    [JsonIgnore]
    public string? Capability => Body == null ? null : Body.Capability(Body.Kind);
}

/// <summary>
/// How a target moves. <see cref="Kind"/> is <c>altaz</c>, <c>major</c>, <c>tle</c> or <c>ephemeris</c>; the other
/// properties belong to one kind each. An agent takes a kind natively when its <c>hello</c> lists
/// <c>target:&lt;kind&gt;</c>; otherwise the service hands it an <c>ephemeris</c> table (if it lists
/// <c>target:ephemeris</c>) or, for the slow kinds, a fixed position at the planned start. Satellites wait for an
/// agent that can follow them.
/// </summary>
public sealed class Body {
    public string Kind { get; set; } = string.Empty;

    /// <summary><c>altaz</c>: a fixed direction over the site.</summary>
    public double? AltDeg { get; set; }

    public double? AzDeg { get; set; }

    /// <summary><c>major</c>: <c>sun</c>, <c>moon</c> or a planet by lower-case name.</summary>
    [JsonPropertyName("body")]
    public string? BodyName { get; set; }

    /// <summary><c>tle</c>: NORAD catalogue number; the element lines are present on assignment.</summary>
    public long? NoradId { get; set; }

    public string? Line1 { get; set; }

    public string? Line2 { get; set; }

    /// <summary>Epoch of the element lines, unix seconds.</summary>
    public double? Epoch { get; set; }

    /// <summary><c>ephemeris</c>: a time-tagged J2000 path to interpolate.</summary>
    public List<EphemerisPoint>? Points { get; set; }

    /// <summary>The capability an agent declares to take targets of a body kind as given.</summary>
    public static string Capability(string kind) => "target:" + kind;

    /// <summary>The body kind a <c>target:&lt;kind&gt;</c> capability names, or null for other capabilities.</summary>
    public static string? CapabilityKind(string capability) =>
        capability.StartsWith("target:", StringComparison.Ordinal) && capability.Length > 7 ? capability[7..] : null;

    /// <summary>J2000 (ra, dec) at <paramref name="unixSeconds"/> by linear interpolation in <see cref="Points"/>; null outside the table.</summary>
    public (double RaDeg, double DecDeg)? Interpolate(double unixSeconds) {
        if (Points == null || Points.Count == 0 || unixSeconds < Points[0].T || unixSeconds > Points[^1].T) {
            return null;
        }

        int lo = 0, hi = Points.Count - 1;
        while (hi - lo > 1) {
            var mid = (lo + hi) / 2;
            if (Points[mid].T <= unixSeconds) {
                lo = mid;
            } else {
                hi = mid;
            }
        }

        var a = Points[lo];
        var b = Points[hi];
        var f = b.T > a.T ? (unixSeconds - a.T) / (b.T - a.T) : 0.0;
        var dra = b.RaDeg - a.RaDeg;
        if (dra > 180) {
            dra -= 360;
        } else if (dra < -180) {
            dra += 360;
        }

        var ra = (a.RaDeg + f * dra) % 360.0;
        return (ra < 0 ? ra + 360.0 : ra, a.DecDeg + f * (b.DecDeg - a.DecDeg));
    }
}

public sealed class EphemerisPoint {
    public double T { get; set; }

    public double RaDeg { get; set; }

    public double DecDeg { get; set; }
}

public sealed class Exposure {
    /// <summary>The filter as a label: one wheel's choice, or the per-wheel choices joined with <c>+</c>.</summary>
    public string Filter { get; set; } = "L";

    public double Seconds { get; set; }

    public int Count { get; set; } = 1;

    /// <summary>Stacked wheels: filter wheel entity id → filter name. Wheels not named are left where they are.</summary>
    public Dictionary<string, string> Filters { get; set; } = new();

    /// <summary>The filter this exposure wants in <paramref name="wheelEntity"/>, or the label when no wheels are named.</summary>
    public string? FilterFor(string? wheelEntity) {
        if (Filters.Count == 0) {
            return Filter;
        }

        return wheelEntity != null && Filters.TryGetValue(wheelEntity, out var f) ? f : null;
    }
}

public sealed class Constraints {
    public double? Earliest { get; set; }

    public double? Latest { get; set; }

    public double? MinAltitudeDeg { get; set; }

    public int Priority { get; set; }
}

/// <summary>
/// A program the agent's own software runs in place of the declarative slew-and-expose: an opaque body in a
/// named format (<c>nina.sequence</c>, <c>nina.target</c>, <c>nina.template</c>, <c>ekos.esq</c>, …). The service
/// only assigns such a task to an agent whose <c>hello</c> declared <c>program:&lt;format&gt;</c>, and inlines the
/// body in <c>task_assign</c>.
/// </summary>
public sealed class ProgramRef {
    public string Id { get; set; } = string.Empty;

    public string Format { get; set; } = string.Empty;

    public long Revision { get; set; }

    /// <summary>SHA-256 of the body, hex.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>The program itself; present on assignment, absent on records.</summary>
    public string? Body { get; set; }

    /// <summary>The capability an agent declares to receive programs of this format.</summary>
    public static string Capability(string format) => "program:" + format;

    /// <summary>The format a <c>program:&lt;format&gt;</c> capability names, or null for other capabilities.</summary>
    public static string? CapabilityFormat(string capability) =>
        capability.StartsWith("program:", StringComparison.Ordinal) && capability.Length > 8 ? capability[8..] : null;
}

/// <summary>
/// A declarative observation. How to slew, focus, flip and guide is the agent's business. With a
/// <see cref="Program"/> the agent runs that instead; the target and exposures are then the service's digest of it.
/// </summary>
public sealed class ObservationTask {
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Which instrument (mount entity id) should run it; null means the site's first.</summary>
    public string? Instrument { get; set; }

    public Target Target { get; set; } = new();

    public List<Exposure> Sequence { get; set; } = new();

    public Constraints Constraints { get; set; } = new();

    public string? Notes { get; set; }

    public ProgramRef? Program { get; set; }

    /// <summary>The capability an agent needs to run this task, when it is a program.</summary>
    [JsonIgnore]
    public string? NeedsCapability => Program == null ? null : ProgramRef.Capability(Program.Format);

    /// <summary>
    /// Every capability an agent needs to take this task exactly as written: the program's format and the
    /// target's body kind (the service may still translate the target for an agent missing the latter).
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> RequiredCapabilities =>
        new[] { NeedsCapability, Target.Capability }.Where(c => c != null).Select(c => c!).ToList();

    [JsonIgnore]
    public double TotalExposureSeconds => Sequence.Sum(e => e.Seconds * e.Count);

    [JsonIgnore]
    public int TotalFrames => Sequence.Sum(e => e.Count);
}

public enum ObservationStatus {
    Queued,
    Accepted,
    Running,
    Completed,
    Failed,
    Cancelled,
    Skipped,
}

public static class ObservationStatusExtensions {
    public static string AsString(this ObservationStatus status) => status switch {
        ObservationStatus.Queued => "queued",
        ObservationStatus.Accepted => "accepted",
        ObservationStatus.Running => "running",
        ObservationStatus.Completed => "completed",
        ObservationStatus.Failed => "failed",
        ObservationStatus.Cancelled => "cancelled",
        ObservationStatus.Skipped => "skipped",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static bool IsTerminal(this ObservationStatus status) =>
        status is ObservationStatus.Completed or ObservationStatus.Failed or ObservationStatus.Cancelled or ObservationStatus.Skipped;

    public static ObservationStatus? Parse(string? s) => s switch {
        "queued" => ObservationStatus.Queued,
        "accepted" => ObservationStatus.Accepted,
        "running" => ObservationStatus.Running,
        "completed" => ObservationStatus.Completed,
        "failed" => ObservationStatus.Failed,
        "cancelled" => ObservationStatus.Cancelled,
        "skipped" => ObservationStatus.Skipped,
        _ => null,
    };
}

using System.Text.Json.Serialization;

namespace ObsServices.Core.Protocol;

public sealed class Target {
    public string Name { get; set; } = string.Empty;

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

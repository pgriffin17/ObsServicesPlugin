using System.Text.Json.Serialization;

namespace ObsServices.Core.Protocol;

public sealed class Target {
    public string Name { get; set; } = string.Empty;

    public double RaDeg { get; set; }

    public double DecDeg { get; set; }
}

public sealed class Exposure {
    public string Filter { get; set; } = "L";

    public double Seconds { get; set; }

    public int Count { get; set; } = 1;
}

public sealed class Constraints {
    public double? Earliest { get; set; }

    public double? Latest { get; set; }

    public double? MinAltitudeDeg { get; set; }

    public int Priority { get; set; }
}

/// <summary>A declarative observation. How to slew, focus, flip and guide is the agent's business.</summary>
public sealed class ObservationTask {
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Which instrument (mount entity id) should run it; null means the site's first.</summary>
    public string? Instrument { get; set; }

    public Target Target { get; set; } = new();

    public List<Exposure> Sequence { get; set; } = new();

    public Constraints Constraints { get; set; } = new();

    public string? Notes { get; set; }

    [JsonIgnore]
    public double TotalExposureSeconds => Sequence.Sum(e => e.Seconds * e.Count);

    [JsonIgnore]
    public int TotalFrames => Sequence.Sum(e => e.Count);
}

[JsonConverter(typeof(JsonStringEnumConverter<ObservationStatus>))]
public enum ObservationStatus {
    [JsonStringEnumMemberName("queued")] Queued,
    [JsonStringEnumMemberName("accepted")] Accepted,
    [JsonStringEnumMemberName("running")] Running,
    [JsonStringEnumMemberName("completed")] Completed,
    [JsonStringEnumMemberName("failed")] Failed,
    [JsonStringEnumMemberName("cancelled")] Cancelled,
    [JsonStringEnumMemberName("skipped")] Skipped,
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

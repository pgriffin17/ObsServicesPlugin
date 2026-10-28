using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ObsServices.Core.Protocol;

/// <summary>Unix seconds as a double, so sub-second stamps survive JSON.</summary>
public static class Timestamp {
    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public static double From(DateTimeOffset t) => t.ToUnixTimeMilliseconds() / 1000.0;
}

[JsonConverter(typeof(JsonStringEnumConverter<Level>))]
public enum Level {
    [JsonStringEnumMemberName("info")] Info,
    [JsonStringEnumMemberName("warn")] Warn,
    [JsonStringEnumMemberName("error")] Error,
}

// ---- agent → service ("up") -----------------------------------------------------------------

/// <summary>Agent → service. Serialized as an object tagged with <c>type</c> (see <see cref="ProtocolJson"/>).</summary>
public abstract class Up {
    internal abstract string Type { get; }
}

public sealed class Hello : Up {
    internal override string Type => "hello";

    public string Agent { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public int Protocol { get; set; } = ProtocolJson.Version;

    public List<string> Capabilities { get; set; } = new();

    /// <summary>
    /// Entities the agent adds (kind <c>device</c>) or annotates: for a known entity id only the
    /// properties are merged.
    /// </summary>
    public List<Entity> Entities { get; set; } = new();
}

/// <summary>A batch of property values: entity id → property → value.</summary>
public sealed class StateMsg : Up {
    internal override string Type => "state";

    public double T { get; set; }

    public Dictionary<string, Dictionary<string, Value>> State { get; set; } = new();

    public StateMsg() {
    }

    public StateMsg(double t) {
        T = t;
    }

    public StateMsg Set(string entity, string property, Value value) {
        if (!State.TryGetValue(entity, out var props)) {
            props = new Dictionary<string, Value>();
            State[entity] = props;
        }

        props[property] = value;
        return this;
    }

    /// <summary>Every value with its flat channel name.</summary>
    public IEnumerable<KeyValuePair<string, Value>> Flat() =>
        State.SelectMany(e => e.Value.Select(p => new KeyValuePair<string, Value>(Channels.Channel(e.Key, p.Key), p.Value)));

    /// <summary>A task status change as the state write it is.</summary>
    public static StateMsg TaskUpdate(string taskId, ObservationStatus status, double? progress, string? message, double t) {
        var m = new StateMsg(t);
        m.Set(taskId, Channels.TaskStatus, status.AsString());

        if (progress is { } p) {
            m.Set(taskId, Channels.TaskProgress, p);
        }

        if (message != null) {
            m.Set(taskId, Channels.TaskMessage, message);
        }

        return m;
    }
}

public sealed class Event : Up {
    internal override string Type => "event";

    public double T { get; set; }

    public Level Level { get; set; } = Level.Info;

    public string Message { get; set; } = string.Empty;
}

public sealed class Ack : Up {
    internal override string Type => "ack";

    public string CommandId { get; set; } = string.Empty;

    public bool Ok { get; set; }

    public string? Message { get; set; }

    public double T { get; set; }
}

// ---- service → agent ("down") ---------------------------------------------------------------

/// <summary>Service → agent. Deserialized from an object tagged with <c>type</c> (see <see cref="ProtocolJson"/>).</summary>
public abstract class Down {
}

public sealed class Welcome : Down {
    public string SiteId { get; set; } = string.Empty;

    public double ServerTime { get; set; }

    public Manifest Manifest { get; set; } = new();
}

/// <summary>The site changed while the agent was connected: the new manifest.</summary>
public sealed class ManifestUpdate : Down {
    public Manifest Manifest { get; set; } = new();
}

public sealed class TaskAssign : Down {
    public ObservationTask Task { get; set; } = new();
}

public sealed class TaskCancel : Down {
    public string TaskId { get; set; } = string.Empty;
}

/// <summary>Desired execution order of the not-yet-started tasks on one instrument.</summary>
public sealed class Plan : Down {
    public string? Instrument { get; set; }

    public List<string> Order { get; set; } = new();
}

/// <summary>
/// One of the few things the service may tell an agent to do directly. <see cref="Kind"/> is
/// <c>stop</c>, <c>park</c>, <c>unpark</c>, <c>open_roof</c>, <c>close_roof</c>, <c>safe</c> or
/// <c>set_time_scale</c>; any further fields (such as <c>factor</c>) land in <see cref="Args"/>.
/// </summary>
public sealed class Command : Down {
    public string Id { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    [JsonExtensionData]
    public Dictionary<string, JsonElement> Args { get; set; } = new();

    /// <summary>A numeric argument, or null when absent or not a number.</summary>
    public double? Number(string name) => Args.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    /// <summary>
    /// The <c>factor</c> of a <c>set_time_scale</c> command. Deliberately not named <c>Factor</c>:
    /// the serializer reserves even ignored property names, which would swallow the JSON key
    /// instead of routing it into <see cref="Args"/>.
    /// </summary>
    [JsonIgnore]
    public double? TimeScaleFactor => Number("factor");
}

public static class CommandKinds {
    public const string Stop = "stop";
    public const string Park = "park";
    public const string Unpark = "unpark";
    public const string OpenRoof = "open_roof";
    public const string CloseRoof = "close_roof";
    public const string Safe = "safe";
    public const string SetTimeScale = "set_time_scale";
}

// ---- converters -----------------------------------------------------------------------------

/// <summary>
/// Handles the abstract <see cref="Up"/> only; concrete messages serialize as plain objects so the
/// converter can wrap them with the <c>type</c> tag.
/// </summary>
public sealed class UpJsonConverter : JsonConverter<Up> {
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(Up);

    public override Up? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        var node = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("expected an object");
        var type = node["type"]?.GetValue<string>() ?? throw new JsonException("missing type");
        node.Remove("type");
        var inner = ProtocolJson.Options;

        return type switch {
            "hello" => node.Deserialize<Hello>(inner),
            "state" => node.Deserialize<StateMsg>(inner),
            "event" => node.Deserialize<Event>(inner),
            "ack" => node.Deserialize<Ack>(inner),
            _ => throw new JsonException($"unknown up message type `{type}`"),
        };
    }

    public override void Write(Utf8JsonWriter writer, Up value, JsonSerializerOptions options) {
        var node = JsonSerializer.SerializeToNode(value, value.GetType(), ProtocolJson.Options) as JsonObject ?? new JsonObject();
        var tagged = new JsonObject { ["type"] = value.Type };

        foreach (var (key, child) in node.ToList()) {
            node.Remove(key);
            tagged[key] = child;
        }

        tagged.WriteTo(writer);
    }
}

public sealed class DownJsonConverter : JsonConverter<Down> {
    public override bool CanConvert(Type typeToConvert) => typeToConvert == typeof(Down);

    public override Down? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        var node = JsonNode.Parse(ref reader) as JsonObject ?? throw new JsonException("expected an object");
        var type = node["type"]?.GetValue<string>() ?? throw new JsonException("missing type");
        node.Remove("type");
        var inner = ProtocolJson.Options;

        return type switch {
            "welcome" => node.Deserialize<Welcome>(inner),
            "manifest" => new ManifestUpdate { Manifest = node.Deserialize<Manifest>(inner) ?? new Manifest() },
            "task_assign" => new TaskAssign { Task = node.Deserialize<ObservationTask>(inner) ?? new ObservationTask() },
            "task_cancel" => node.Deserialize<TaskCancel>(inner),
            "plan" => node.Deserialize<Plan>(inner),
            "command" => node.Deserialize<Command>(inner),
            _ => throw new JsonException($"unknown down message type `{type}`"),
        };
    }

    public override void Write(Utf8JsonWriter writer, Down value, JsonSerializerOptions options) {
        var (type, payload) = value switch {
            Welcome w => ("welcome", (object)w),
            ManifestUpdate m => ("manifest", m.Manifest),
            TaskAssign t => ("task_assign", t.Task),
            TaskCancel c => ("task_cancel", c),
            Plan p => ("plan", p),
            Command c => ("command", c),
            _ => throw new JsonException($"unknown down message {value.GetType().Name}"),
        };
        var node = JsonSerializer.SerializeToNode(payload, payload.GetType(), ProtocolJson.Options) as JsonObject ?? new JsonObject();
        var tagged = new JsonObject { ["type"] = type };

        foreach (var (key, child) in node.ToList()) {
            node.Remove(key);
            tagged[key] = child;
        }

        tagged.WriteTo(writer);
    }
}

/// <summary>Serializer settings shared by every protocol type.</summary>
public static class ProtocolJson {
    /// <summary>Wire protocol version this library speaks.</summary>
    public const int Version = 2;

    public static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new UpJsonConverter(), new DownJsonConverter() },
    };

    public static string Encode(Up message) => JsonSerializer.Serialize<Up>(message, Options);

    public static string Encode(Down message) => JsonSerializer.Serialize<Down>(message, Options);

    public static Down DecodeDown(string json) => JsonSerializer.Deserialize<Down>(json, Options) ?? throw new JsonException("null message");

    public static Up DecodeUp(string json) => JsonSerializer.Deserialize<Up>(json, Options) ?? throw new JsonException("null message");
}

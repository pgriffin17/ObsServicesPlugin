using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ObsServices.Core.Protocol;

/// <summary>Unix seconds as a double, so sub-second stamps survive JSON.</summary>
public static class Timestamp {
    public static double Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public static double From(DateTimeOffset t) => t.ToUnixTimeMilliseconds() / 1000.0;
}

public enum Level {
    Info,
    Warn,
    Error,
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

    /// <summary>For <c>set</c> commands: how far it got.</summary>
    public SetStatus? Status { get; set; }

    /// <summary>For <c>set</c> commands: what the hardware actually settled on.</summary>
    public Dictionary<string, Value>? Observed { get; set; }

    public double T { get; set; }

    /// <summary>A reply to a <c>set</c>; <c>ok</c> follows from the status.</summary>
    public static Ack ForSet(string commandId, SetStatus status, string? message, Dictionary<string, Value>? observed, double t) =>
        new() { CommandId = commandId, Ok = status is SetStatus.Accepted or SetStatus.Done, Message = message, Status = status, Observed = observed, T = t };
}

/// <summary>
/// How an agent answered a <c>set</c>. <c>accepted</c> is followed by a second ack for the same
/// command once the value is reached; every other status is final.
/// </summary>
public enum SetStatus {
    /// <summary>Taken; a <c>done</c> (or <c>rejected</c>) ack follows.</summary>
    Accepted,
    /// <summary>Reached; <c>observed</c> says what the hardware settled on.</summary>
    Done,
    /// <summary>Would not or could not: the message says why.</summary>
    Rejected,
    /// <summary>The agent has no device behind that entity or property.</summary>
    Unbound,
    /// <summary>The agent knows the property but cannot write it.</summary>
    Unsupported,
    /// <summary>A task is running on that instrument and the agent will not interleave.</summary>
    Busy,
    /// <summary>A newer set for the same property replaced it (issued by the service).</summary>
    Superseded,
    /// <summary>No answer in time (issued by the service).</summary>
    Timeout,
}

/// <summary>What an agent answers a <c>set</c> with. See <see cref="SetStatus"/>.</summary>
public sealed record SetReply(SetStatus Status, string? Message = null, Dictionary<string, Value>? Observed = null) {
    public static SetReply Done(string? message = null, Dictionary<string, Value>? observed = null) => new(SetStatus.Done, message, observed);

    public static SetReply Done(string? message, string property, Value value) => new(SetStatus.Done, message, new Dictionary<string, Value> { [property] = value });

    public static SetReply Accepted(string? message = null) => new(SetStatus.Accepted, message);

    public static SetReply Rejected(string message) => new(SetStatus.Rejected, message);

    public static SetReply Unbound(string message = "no device behind that entity") => new(SetStatus.Unbound, message);

    public static SetReply Unsupported(string message = "cannot be set") => new(SetStatus.Unsupported, message);

    public static SetReply Busy(string message = "a task is running") => new(SetStatus.Busy, message);
}

/// <summary>Entities the agent adds or annotates after <c>hello</c>: the same list as <c>hello.entities</c>.</summary>
public sealed class EntitiesMsg : Up {
    internal override string Type => "entities";

    public List<Entity> Entities { get; set; } = new();
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

    /// <summary>A <c>set</c> command's target entity, or null for other kinds.</summary>
    [JsonIgnore]
    public string? SetEntity => Kind == CommandKinds.Set && Args.TryGetValue("entity", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;

    /// <summary>A <c>set</c> command's requested values, empty for other kinds.</summary>
    public Dictionary<string, Value> SetValues() {
        var result = new Dictionary<string, Value>();

        if (Kind != CommandKinds.Set || !Args.TryGetValue("values", out var values) || values.ValueKind != JsonValueKind.Object) {
            return result;
        }

        foreach (var p in values.EnumerateObject()) {
            result[p.Name] = p.Value.ValueKind switch {
                JsonValueKind.Number => Value.Number(p.Value.GetDouble()),
                JsonValueKind.True => Value.Bool(true),
                JsonValueKind.False => Value.Bool(false),
                JsonValueKind.String => Value.Text(p.Value.GetString() ?? string.Empty),
                _ => Value.Text(p.Value.GetRawText()),
            };
        }

        return result;
    }
}

public static class CommandKinds {
    public const string Stop = "stop";
    public const string Park = "park";
    public const string Unpark = "unpark";
    public const string OpenRoof = "open_roof";
    public const string CloseRoof = "close_roof";
    public const string Safe = "safe";
    public const string SetTimeScale = "set_time_scale";

    /// <summary>Set one or more properties of one entity; answered with a <see cref="SetStatus"/>.</summary>
    public const string Set = "set";
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
            "entities" => node.Deserialize<EntitiesMsg>(inner),
            "image_captured" => node.Deserialize<ImageCaptured>(inner),
            "image_inventory" => node.Deserialize<ImageInventory>(inner),
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
            "image_grant" => node.Deserialize<ImageGrant>(inner),
            "image_release" => node.Deserialize<ImageRelease>(inner),
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
            ImageGrant g => ("image_grant", g),
            ImageRelease r => ("image_release", r),
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
    public const int Version = 6;

    public static readonly JsonSerializerOptions Options = new() {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower), new UpJsonConverter(), new DownJsonConverter() },
    };

    public static string Encode(Up message) => JsonSerializer.Serialize<Up>(message, Options);

    public static string Encode(Down message) => JsonSerializer.Serialize<Down>(message, Options);

    public static Down DecodeDown(string json) => JsonSerializer.Deserialize<Down>(json, Options) ?? throw new JsonException("null message");

    public static Up DecodeUp(string json) => JsonSerializer.Deserialize<Up>(json, Options) ?? throw new JsonException("null message");
}

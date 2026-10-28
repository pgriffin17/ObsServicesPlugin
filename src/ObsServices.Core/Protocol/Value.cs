using System.Text.Json;
using System.Text.Json.Serialization;

namespace ObsServices.Core.Protocol;

/// <summary>
/// A telemetry value: a number, a boolean or text. On the wire it is untagged (a bare JSON
/// number, boolean or string), matching the service's <c>Value</c> enum.
/// </summary>
[JsonConverter(typeof(ValueJsonConverter))]
public readonly record struct Value {
    private readonly double number;
    private readonly bool flag;
    private readonly string? text;

    public ValueKind Kind { get; }

    private Value(ValueKind kind, double number, bool flag, string? text) {
        Kind = kind;
        this.number = number;
        this.flag = flag;
        this.text = text;
    }

    public static Value Number(double n) => new(ValueKind.Number, n, false, null);

    public static Value Bool(bool b) => new(ValueKind.Bool, 0, b, null);

    public static Value Text(string s) => new(ValueKind.Text, 0, false, s ?? throw new ArgumentNullException(nameof(s)));

    public static implicit operator Value(double n) => Number(n);

    public static implicit operator Value(int n) => Number(n);

    public static implicit operator Value(bool b) => Bool(b);

    public static implicit operator Value(string s) => Text(s);

    /// <summary>The number, or 1/0 for a boolean; null for text (same rule as the service).</summary>
    public double? AsDouble() => Kind switch {
        ValueKind.Number => number,
        ValueKind.Bool => flag ? 1.0 : 0.0,
        _ => null,
    };

    public bool? AsBool() => Kind == ValueKind.Bool ? flag : null;

    public string? AsText() => Kind == ValueKind.Text ? text : null;

    public override string ToString() => Kind switch {
        ValueKind.Number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ValueKind.Bool => flag ? "true" : "false",
        _ => text ?? string.Empty,
    };
}

public enum ValueKind {
    Number,
    Bool,
    Text,
}

public sealed class ValueJsonConverter : JsonConverter<Value> {
    public override Value Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) {
        return reader.TokenType switch {
            JsonTokenType.Number => Value.Number(reader.GetDouble()),
            JsonTokenType.True => Value.Bool(true),
            JsonTokenType.False => Value.Bool(false),
            JsonTokenType.String => Value.Text(reader.GetString() ?? string.Empty),
            _ => throw new JsonException($"a telemetry value must be a number, boolean or string, not {reader.TokenType}"),
        };
    }

    public override void Write(Utf8JsonWriter writer, Value value, JsonSerializerOptions options) {
        switch (value.Kind) {
            case ValueKind.Number:
                writer.WriteNumberValue(value.AsDouble()!.Value);
                break;
            case ValueKind.Bool:
                writer.WriteBooleanValue(value.AsBool()!.Value);
                break;
            default:
                writer.WriteStringValue(value.AsText());
                break;
        }
    }
}

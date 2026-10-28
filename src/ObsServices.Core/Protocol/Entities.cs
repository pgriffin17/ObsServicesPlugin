using System.Text.Json.Serialization;

namespace ObsServices.Core.Protocol;

[JsonConverter(typeof(JsonStringEnumConverter<PropertyKind>))]
public enum PropertyKind {
    [JsonStringEnumMemberName("number")] Number,
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("bool")] Bool,
}

[JsonConverter(typeof(JsonStringEnumConverter<EntityKind>))]
public enum EntityKind {
    /// <summary>The site as a whole (sidereal time, site-wide readings).</summary>
    [JsonStringEnumMemberName("site")] Site,
    /// <summary>The agent process (its clock, time scale).</summary>
    [JsonStringEnumMemberName("agent")] Agent,
    /// <summary>An instance of a library component in the site's assembly.</summary>
    [JsonStringEnumMemberName("component")] Component,
    /// <summary>Something the agent drives that is not modelled in the assembly.</summary>
    [JsonStringEnumMemberName("device")] Device,
    /// <summary>An observation task.</summary>
    [JsonStringEnumMemberName("task")] Task,
}

/// <summary>A property an entity publishes.</summary>
public sealed class PropertyInfo {
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public PropertyKind Kind { get; set; } = PropertyKind.Number;

    public string? Unit { get; set; }

    public string? Description { get; set; }

    /// <summary>Allowed values of a text property (informational).</summary>
    public List<string> Values { get; set; } = new();

    /// <summary>The assembly joint this property drives (component entities).</summary>
    public string? Joint { get; set; }

    public static PropertyInfo Number(string name, string? unit = null, string? description = null) =>
        new() { Name = name, Kind = PropertyKind.Number, Unit = string.IsNullOrEmpty(unit) ? null : unit, Description = description };

    public static PropertyInfo Text(string name, IEnumerable<string>? values = null, string? description = null) =>
        new() { Name = name, Kind = PropertyKind.Text, Values = values?.ToList() ?? new List<string>(), Description = description };
}

/// <summary>
/// Something with identity and state. <see cref="Id"/> is a UUID minted per site; <see cref="Name"/>
/// is the label every surface shows instead of it.
/// </summary>
public sealed class Entity {
    public string Id { get; set; } = string.Empty;

    public EntityKind Kind { get; set; } = EntityKind.Component;

    public string Name { get; set; } = string.Empty;

    /// <summary>Component definition id (kind <c>component</c>).</summary>
    public string? Component { get; set; }

    /// <summary>Assembly instance name (kind <c>component</c>).</summary>
    public string? Instance { get; set; }

    /// <summary>Entity this one is mated under in the assembly, when any.</summary>
    public string? Parent { get; set; }

    public List<PropertyInfo> Properties { get; set; } = new();

    public PropertyInfo? Property(string name) => Properties.FirstOrDefault(p => p.Name == name);

    /// <summary>Names of the joint properties: the axes an agent is expected to publish.</summary>
    public IReadOnlyList<string> Joints() => Properties.Where(p => p.Joint != null).Select(p => p.Name).ToList();
}

/// <summary>A schedulable telescope chain: a mount entity (whose id is the instrument's id) and what hangs under it.</summary>
public sealed class InstrumentInfo {
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Mount entity (same as <see cref="Id"/>).</summary>
    public string Mount { get; set; } = string.Empty;

    /// <summary>Objective entity of the primary optical train.</summary>
    public string? Optics { get; set; }

    /// <summary>Sensor entity at the end of the primary imaging train.</summary>
    public string? Camera { get; set; }
}

/// <summary>What a site is made of, as the agent needs to see it. Arrives in <c>welcome</c>.</summary>
public sealed class Manifest {
    public List<Entity> Entities { get; set; } = new();

    public List<InstrumentInfo> Instruments { get; set; } = new();

    public Entity? Entity(string id) => Entities.FirstOrDefault(e => e.Id == id);

    public IEnumerable<Entity> OfKind(EntityKind kind) => Entities.Where(e => e.Kind == kind);

    /// <summary>The component entity for an assembly instance name.</summary>
    public Entity? Instance(string name) => Entities.FirstOrDefault(e => e.Kind == EntityKind.Component && e.Instance == name);

    /// <summary>Component entities mated (directly or further down) under <paramref name="id"/>.</summary>
    public List<Entity> Under(string id) {
        var result = new List<Entity>();

        foreach (var e in Entities) {
            var current = e.Parent;
            var hops = 0;

            while (current != null && hops < 32) {
                if (current == id) {
                    result.Add(e);
                    break;
                }

                current = Entity(current)?.Parent;
                hops++;
            }
        }

        return result;
    }

    /// <summary>By id, by the instrument's name, or by the mount's assembly instance name.</summary>
    public InstrumentInfo? Instrument(string key) {
        foreach (var i in Instruments) {
            if (i.Id == key || i.Name == key) {
                return i;
            }

            if (Entity(i.Mount)?.Instance == key) {
                return i;
            }
        }

        return null;
    }
}

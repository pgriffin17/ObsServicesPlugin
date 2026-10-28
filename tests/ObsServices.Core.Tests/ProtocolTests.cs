using System.Text.Json;
using NUnit.Framework;
using ObsServices.Core;
using ObsServices.Core.Protocol;

namespace ObsServices.Core.Tests;

/// <summary>The wire contract, mirroring the service's own `wire.rs` and protocol unit tests.</summary>
public class ProtocolTests {
    [Test]
    public void StateMessagesRoundTripTagged() {
        var m = new StateMsg(1.0);
        m.Set("m1", "ra", 12.5).Set("c1", "state", "idle").Set("c1", "cooler", true);
        var json = ProtocolJson.Encode(m);

        Assert.That(json, Does.StartWith("{\"type\":\"state\""));
        Assert.That(json, Does.Contain("\"m1\":{\"ra\":12.5}"));
        Assert.That(json, Does.Contain("\"state\":\"idle\""));
        Assert.That(json, Does.Contain("\"cooler\":true"));

        var back = (StateMsg)ProtocolJson.DecodeUp(json);
        Assert.That(back.T, Is.EqualTo(1.0));
        Assert.That(back.State["m1"]["ra"].AsDouble(), Is.EqualTo(12.5));
        Assert.That(back.State["c1"]["state"].AsText(), Is.EqualTo("idle"));
        Assert.That(back.State["c1"]["cooler"].AsBool(), Is.True);
        Assert.That(back.Flat().Select(kv => kv.Key), Is.EquivalentTo(new[] { "m1.ra", "c1.state", "c1.cooler" }));
    }

    [Test]
    public void HelloCarriesIdentityAndDeclaredEntities() {
        var hello = new Hello {
            Agent = "nina",
            Version = "1.0.0.0",
            Capabilities = { "mount", "camera" },
            Entities = { new Entity { Id = "dev-1", Kind = EntityKind.Device, Name = "weather", Properties = { PropertyInfo.Number("sky_temp_c", "C", "Sky temperature") } } },
        };
        var json = ProtocolJson.Encode(hello);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.That(root.GetProperty("type").GetString(), Is.EqualTo("hello"));
        Assert.That(root.GetProperty("protocol").GetInt32(), Is.EqualTo(2));
        Assert.That(root.GetProperty("entities")[0].GetProperty("kind").GetString(), Is.EqualTo("device"));
        Assert.That(root.GetProperty("entities")[0].GetProperty("properties")[0].GetProperty("type").GetString(), Is.EqualTo("number"));
        Assert.That(root.GetProperty("entities")[0].GetProperty("properties")[0].GetProperty("unit").GetString(), Is.EqualTo("C"));
        Assert.That(root.GetProperty("entities")[0].TryGetProperty("parent", out _), Is.False, "nulls are omitted");
    }

    [Test]
    public void TaskUpdatesAreStateWrites() {
        var up = StateMsg.TaskUpdate("t1", ObservationStatus.Running, 0.5, "half", 2.0);

        Assert.That(up.State["t1"]["status"].AsText(), Is.EqualTo("running"));
        Assert.That(up.State["t1"]["progress"].AsDouble(), Is.EqualTo(0.5));
        Assert.That(ObservationStatusExtensions.Parse("running"), Is.EqualTo(ObservationStatus.Running));
        Assert.That(ObservationStatusExtensions.Parse("flying"), Is.Null);
        Assert.That(ObservationStatus.Completed.IsTerminal(), Is.True);
        Assert.That(ObservationStatus.Accepted.IsTerminal(), Is.False);
    }

    [Test]
    public void AckAndEventSerializeWithSnakeCaseNames() {
        var ack = ProtocolJson.Encode(new Ack { CommandId = "c1", Ok = false, Message = "no", T = 3.0 });
        Assert.That(ack, Is.EqualTo("{\"type\":\"ack\",\"command_id\":\"c1\",\"ok\":false,\"message\":\"no\",\"t\":3}"));

        var ev = ProtocolJson.Encode(new Event { T = 4.0, Level = Level.Warn, Message = "roof stuck" });
        Assert.That(ev, Is.EqualTo("{\"type\":\"event\",\"t\":4,\"level\":\"warn\",\"message\":\"roof stuck\"}"));
    }

    [Test]
    public void WelcomeDecodesTheManifest() {
        const string json = """
            {"type":"welcome","site_id":"oakland","server_time":1700000000.5,"manifest":{
              "entities":[
                {"id":"s","kind":"site","name":"Oakland","properties":[{"name":"lst_deg","type":"number","unit":"deg"}]},
                {"id":"m","kind":"component","name":"mount","component":"gem_mount","instance":"mount",
                 "properties":[{"name":"ra","joint":"ra"},{"name":"dec","joint":"dec"},{"name":"mode","type":"text","values":["parked","tracking"]}]},
                {"id":"o","kind":"component","name":"ota","instance":"ota","parent":"m","properties":[]},
                {"id":"c","kind":"component","name":"cam","instance":"cam","parent":"o","properties":[{"name":"state","type":"text"}]}
              ],
              "instruments":[{"id":"m","name":"mount","mount":"m","optics":"o","camera":"c"}]}}
            """;
        var welcome = (Welcome)ProtocolJson.DecodeDown(json);

        Assert.That(welcome.SiteId, Is.EqualTo("oakland"));
        Assert.That(welcome.ServerTime, Is.EqualTo(1700000000.5));
        var manifest = welcome.Manifest;
        Assert.That(manifest.Entity("m")!.Joints(), Is.EqualTo(new[] { "ra", "dec" }));
        Assert.That(manifest.Entity("m")!.Property("mode")!.Kind, Is.EqualTo(PropertyKind.Text));
        Assert.That(manifest.Instance("cam")!.Id, Is.EqualTo("c"));
        Assert.That(manifest.Under("m").Select(e => e.Id), Is.EquivalentTo(new[] { "o", "c" }));
        Assert.That(manifest.Instrument("mount")!.Camera, Is.EqualTo("c"));
        Assert.That(manifest.OfKind(EntityKind.Site).Single().Id, Is.EqualTo("s"));
    }

    [Test]
    public void DownMessagesDecodeByTag() {
        var assign = (TaskAssign)ProtocolJson.DecodeDown("""{"type":"task_assign","id":"t1","name":"M31","instrument":"m","target":{"name":"M31","ra_deg":10.68,"dec_deg":41.27},"sequence":[{"filter":"Ha","seconds":300,"count":6},{"seconds":120}],"constraints":{"min_altitude_deg":30,"priority":2}}""");
        Assert.That(assign.Task.Id, Is.EqualTo("t1"));
        Assert.That(assign.Task.Target.RaDeg, Is.EqualTo(10.68));
        Assert.That(assign.Task.Sequence[1].Filter, Is.EqualTo("L"), "filter defaults to L");
        Assert.That(assign.Task.Sequence[1].Count, Is.EqualTo(1), "count defaults to 1");
        Assert.That(assign.Task.Constraints.MinAltitudeDeg, Is.EqualTo(30));
        Assert.That(assign.Task.Constraints.Earliest, Is.Null);
        Assert.That(assign.Task.TotalExposureSeconds, Is.EqualTo(300 * 6 + 120));
        Assert.That(assign.Task.TotalFrames, Is.EqualTo(7));

        var cancel = (TaskCancel)ProtocolJson.DecodeDown("""{"type":"task_cancel","task_id":"t1"}""");
        Assert.That(cancel.TaskId, Is.EqualTo("t1"));

        var plan = (Plan)ProtocolJson.DecodeDown("""{"type":"plan","instrument":null,"order":["a","b"]}""");
        Assert.That(plan.Instrument, Is.Null);
        Assert.That(plan.Order, Is.EqualTo(new[] { "a", "b" }));

        var manifest = (ManifestUpdate)ProtocolJson.DecodeDown("""{"type":"manifest","entities":[{"id":"x","kind":"device","name":"x"}],"instruments":[]}""");
        Assert.That(manifest.Manifest.Entities.Single().Kind, Is.EqualTo(EntityKind.Device));
    }

    [Test]
    public void CommandsCarryTheirKindAndFlattenedArguments() {
        var park = (Command)ProtocolJson.DecodeDown("""{"type":"command","id":"c1","kind":"park"}""");
        Assert.That(park.Kind, Is.EqualTo(CommandKinds.Park));
        Assert.That(park.TimeScaleFactor, Is.Null);

        var scale = (Command)ProtocolJson.DecodeDown("""{"type":"command","id":"c2","kind":"set_time_scale","factor":30}""");
        Assert.That(scale.Kind, Is.EqualTo(CommandKinds.SetTimeScale));
        Assert.That(scale.TimeScaleFactor, Is.EqualTo(30.0));
        Assert.That(scale.Args.Keys, Is.EquivalentTo(new[] { "factor" }), "id and kind are typed, the rest is arguments");

        // what the service's serde emits, reproduced for the round trip
        var json = ProtocolJson.Encode(new Command { Id = "c2", Kind = "set_time_scale", Args = { ["factor"] = JsonSerializer.SerializeToElement(30.0) } });
        Assert.That(json, Is.EqualTo("{\"type\":\"command\",\"id\":\"c2\",\"kind\":\"set_time_scale\",\"factor\":30}"));
    }

    [Test]
    public void UnknownTagsAreRejected() {
        Assert.Throws<JsonException>(() => ProtocolJson.DecodeDown("""{"type":"teleport","to":"mars"}"""));
        Assert.Throws<JsonException>(() => ProtocolJson.DecodeDown("""{"no":"type"}"""));
    }

    [Test]
    public void ChannelsSplitOnTheLastDot() {
        Assert.That(Channels.Channel("abc", "ra"), Is.EqualTo("abc.ra"));
        Assert.That(Channels.SplitChannel("abc.def.ra"), Is.EqualTo(("abc.def", "ra")));
        Assert.That(Channels.SplitChannel("ra"), Is.EqualTo(("", "ra")));
    }

    [Test]
    public void DerivedEntityIdsMatchTheService() {
        // reference values from observatory.channels (Python) for site "oakland"
        Assert.That(Channels.SiteEntityId("oakland"), Is.EqualTo("077a09d4-4235-5f8d-b22c-d1fab8d6d136"));
        Assert.That(Channels.AgentEntityId("oakland"), Is.EqualTo("3601a47d-ef2c-5793-8be0-53035257c6fe"));
        Assert.That(Channels.DeviceEntityId("oakland", "weather"), Is.EqualTo("5e54b3b1-f289-5b0a-88a0-77985a3d924f"));
    }
}

public class AstroTests {
    [Test]
    public void LocalSiderealTimeMatchesTheReferenceAgent() {
        // observatory.agent._lst_deg(1700000000.0, -122.2712)
        Assert.That(Astro.LstDeg(1700000000.0, -122.2712), Is.EqualTo(264.81632585823536).Within(1e-9));
    }

    [Test]
    public void AltAzMatchesTheReferenceAgent() {
        // observatory.agent._alt_az(30.0, 45.0, 37.8044)
        var (alt, az) = Astro.AltAz(30.0, 45.0, 37.8044);
        Assert.That(alt, Is.EqualTo(66.5307526015283).Within(1e-9));
        Assert.That(az, Is.EqualTo(297.40785140950777).Within(1e-9));
    }

    [Test]
    public void HourAngleIsWestPositiveInHalfOpenRange() {
        Assert.That(Astro.HourAngleDeg(100, 90), Is.EqualTo(10));
        Assert.That(Astro.HourAngleDeg(90, 100), Is.EqualTo(-10));
        Assert.That(Astro.HourAngleDeg(0, 180), Is.EqualTo(180));
        Assert.That(Astro.HourAngleDeg(10, 350), Is.EqualTo(20));
        Assert.That(Astro.RaDeg(100, Astro.HourAngleDeg(100, 250)), Is.EqualTo(250).Within(1e-12));
        Assert.That(Astro.HoursToDeg(5.588), Is.EqualTo(83.82).Within(1e-12));
    }
}

using NUnit.Framework;
using ObsServices.Core;
using ObsServices.Core.Agent;
using ObsServices.Core.Devices;
using ObsServices.Core.Protocol;
using ObsServices.Core.Tests.Fakes;

namespace ObsServices.Core.Tests;

/// <summary>Binding and telemetry mapping, without a server.</summary>
public class SiteBindingTests {
    private static Manifest TwoInstrumentSite() => new() {
        Entities = {
            new Entity { Id = "shed", Kind = EntityKind.Component, Name = "shed", Instance = "shed", Properties = { new PropertyInfo { Name = "roof", Joint = "roof" }, PropertyInfo.Text("state") } },
            new Entity { Id = "m1", Kind = EntityKind.Component, Name = "mount", Instance = "mount", Properties = { new PropertyInfo { Name = "ra", Joint = "ra" }, new PropertyInfo { Name = "dec", Joint = "dec" }, PropertyInfo.Text("mode") } },
            new Entity { Id = "ota1", Kind = EntityKind.Component, Name = "ota", Instance = "ota", Parent = "m1" },
            new Entity { Id = "fw1", Kind = EntityKind.Component, Name = "wheel", Instance = "wheel", Parent = "ota1", Properties = { new PropertyInfo { Name = "wheel", Joint = "wheel" }, PropertyInfo.Text("filter") } },
            new Entity { Id = "foc1", Kind = EntityKind.Component, Name = "focuser", Instance = "focuser", Parent = "ota1", Properties = { new PropertyInfo { Name = "focus", Joint = "focus" } } },
            new Entity { Id = "cam1", Kind = EntityKind.Component, Name = "cam", Instance = "cam", Parent = "foc1", Properties = { PropertyInfo.Text("state") } },
            new Entity { Id = "m2", Kind = EntityKind.Component, Name = "altaz", Instance = "altaz", Properties = { new PropertyInfo { Name = "azimuth", Joint = "azimuth" }, new PropertyInfo { Name = "elevation", Joint = "elevation" } } },
        },
        Instruments = {
            new InstrumentInfo { Id = "m1", Name = "mount", Mount = "m1", Optics = "ota1", Camera = "cam1" },
            new InstrumentInfo { Id = "m2", Name = "altaz", Mount = "m2" },
        },
    };

    [Test]
    public void BindsTheFirstInstrumentByDefault() {
        var b = SiteBinding.FromManifest(TwoInstrumentSite());

        Assert.That(b.InstrumentId, Is.EqualTo("m1"));
        Assert.That(b.MountEntity, Is.EqualTo("m1"));
        Assert.That(b.IsAltAz, Is.False);
        Assert.That((b.HaJoint, b.DecJoint), Is.EqualTo(("ra", "dec")));
        Assert.That(b.CameraEntity, Is.EqualTo("cam1"));
        Assert.That(b.FilterWheelEntity, Is.EqualTo("fw1"), "the wheel is found under the mount by its filter property");
        Assert.That(b.FocuserEntity, Is.EqualTo("foc1"), "the focuser by its focus joint");
        Assert.That(b.RoofEntity, Is.EqualTo("shed"));
        Assert.That(b.RoofJoints, Is.EqualTo(new[] { "roof" }));
    }

    [Test]
    public void BindsAnAltAzInstrumentByNameOrInstance() {
        foreach (var key in new[] { "m2", "altaz" }) {
            var b = SiteBinding.FromManifest(TwoInstrumentSite(), key);
            Assert.That(b.InstrumentId, Is.EqualTo("m2"), key);
            Assert.That(b.IsAltAz, Is.True);
            Assert.That((b.HaJoint, b.DecJoint), Is.EqualTo(("azimuth", "elevation")));
            Assert.That(b.CameraEntity, Is.Null);
            Assert.That(b.RoofEntity, Is.EqualTo("shed"), "the roof is shared");
        }
    }

    [Test]
    public void UnknownInstrumentBindsOnlyTheRoof() {
        var b = SiteBinding.FromManifest(TwoInstrumentSite(), "nope");
        Assert.That(b.MountEntity, Is.Null);
        Assert.That(b.RoofEntity, Is.EqualTo("shed"));
    }
}

/// <summary>Telemetry mapping through a bound agent with fake devices, read back through the protocol.</summary>
public class ObservatoryAgentStateTests {
    private sealed class Probe : ObservatoryAgent {
        public Probe(IDeviceSet devices) : base("http://localhost:1", "site", "tca_x", devices) {
        }

        public Task Bind(Manifest m) => (Task)typeof(ObservatoryAgent).GetMethod("OnManifestAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(this, new object[] { m, CancellationToken.None })!;

        public Task<StateMsg?> Sample() => (Task<StateMsg?>)typeof(ObservatoryAgent).GetMethod("StateAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(this, new object[] { CancellationToken.None })!;
    }

    [Test]
    public async Task MountStateBecomesHourAngleJointsAndMode() {
        var clock = new FakeClock();
        var mount = new FakeMount(clock, 37.8, -122.3);
        var camera = new FakeCamera(clock);
        var wheel = new FakeFilterWheel(clock, "L", "R");
        var roof = new FakeRoof(clock);
        var agent = new Probe(new DeviceSet { Mount = mount, Camera = camera, FilterWheel = wheel, Roof = roof, Focuser = new FakeFocuser { Position = 1234 } });
        var manifest = new Manifest {
            Entities = {
                new Entity { Id = "shed", Kind = EntityKind.Component, Instance = "shed", Properties = { new PropertyInfo { Name = "roof", Joint = "roof" } } },
                new Entity { Id = "m", Kind = EntityKind.Component, Instance = "mount", Properties = { new PropertyInfo { Name = "ra", Joint = "ra" }, new PropertyInfo { Name = "dec", Joint = "dec" } } },
                new Entity { Id = "fw", Kind = EntityKind.Component, Instance = "fw", Parent = "m", Properties = { PropertyInfo.Text("filter") } },
                new Entity { Id = "foc", Kind = EntityKind.Component, Instance = "foc", Parent = "m", Properties = { new PropertyInfo { Name = "focus", Joint = "focus" } } },
                new Entity { Id = "cam", Kind = EntityKind.Component, Instance = "cam", Parent = "m" },
            },
            Instruments = { new InstrumentInfo { Id = "m", Mount = "m", Camera = "cam" } },
        };
        await agent.Bind(manifest);

        var parked = (await agent.Sample())!;
        Assert.That(parked.State["m"]["mode"].AsText(), Is.EqualTo("parked"));
        Assert.That(parked.State["shed"]["state"].AsText(), Is.EqualTo("closed"));
        Assert.That(parked.State["shed"]["roof"].AsDouble(), Is.EqualTo(0));
        Assert.That(parked.State["fw"]["filter"].AsText(), Is.EqualTo("L"));
        Assert.That(parked.State["foc"]["focus"].AsDouble(), Is.EqualTo(1234));
        Assert.That(parked.State["cam"]["state"].AsText(), Is.EqualTo("idle"));
        Assert.That(parked.State[Channels.SiteEntityId("site")]["lst_deg"].AsDouble(), Is.Not.Null, "sidereal time comes from the mount");

        await mount.UnparkAsync(default);
        await mount.SlewAsync(37.95, 89.26, default);
        await roof.OpenAsync(default);
        var tracking = (await agent.Sample())!;
        var reading = mount.Read();
        Assert.That(tracking.State["m"]["mode"].AsText(), Is.EqualTo("tracking"));
        Assert.That(tracking.State["m"]["ra"].AsDouble(), Is.EqualTo(Astro.HourAngleDeg(reading.SiderealTimeDeg!.Value, 37.95)).Within(0.01), "the ra joint is the hour angle");
        Assert.That(tracking.State["m"]["dec"].AsDouble(), Is.EqualTo(89.26).Within(1e-9));
        Assert.That(tracking.State["m"]["ra_deg"].AsDouble(), Is.EqualTo(37.95).Within(0.01), "ra_deg is the sky right ascension");
        Assert.That(tracking.State["shed"]["state"].AsText(), Is.EqualTo("open"));
        Assert.That(tracking.State["shed"]["roof"].AsDouble(), Is.EqualTo(2400));

        mount.Connected = false;
        camera.Connected = false;
        var offline = (await agent.Sample())!;
        Assert.That(offline.State.ContainsKey("m"), Is.False, "disconnected devices publish nothing");
        Assert.That(offline.State.ContainsKey("cam"), Is.False);
        Assert.That(offline.State.ContainsKey("shed"), Is.True);
    }
}

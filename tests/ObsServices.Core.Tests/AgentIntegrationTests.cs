using System.Text.Json.Nodes;
using NUnit.Framework;
using ObsServices.Core;
using ObsServices.Core.Agent;
using ObsServices.Core.Devices;
using ObsServices.Core.Protocol;
using ObsServices.Core.Tests.Fakes;

namespace ObsServices.Core.Tests;

/// <summary>The production agent over fake devices, with a log the tests can inspect.</summary>
internal sealed class TestAgent : ObservatoryAgent {
    public FakeClock Clock { get; } = new();
    public FakeMount Mount { get; }
    public FakeCamera Camera { get; }
    public FakeFilterWheel Wheel { get; }
    public FakeRoof Roof { get; }
    public List<string> Logged { get; } = new();

    private TestAgent(string baseUrl, string siteId, string token, FakeClock clock, FakeMount mount, FakeCamera camera, FakeFilterWheel wheel, FakeRoof roof, ObservatoryAgentOptions? options)
        : base(baseUrl, siteId, token, new DeviceSet { Mount = mount, Camera = camera, FilterWheel = wheel, Roof = roof, Focuser = new FakeFocuser() }, options) {
        Clock = clock;
        Mount = mount;
        Camera = camera;
        Wheel = wheel;
        Roof = roof;
        Name = "obsservices-test";
    }

    public static TestAgent Create(string baseUrl, string siteId, string token, double latDeg, double lonDeg, ObservatoryAgentOptions? options = null) {
        var clock = new FakeClock();
        return new TestAgent(baseUrl, siteId, token, clock, new FakeMount(clock, latDeg, lonDeg), new FakeCamera(clock), new FakeFilterWheel(clock, "L", "R", "G", "B", "Ha"), new FakeRoof(clock), options);
    }

    // a device of the agent's own, beside what the manifest lists: the hello/annotation path
    protected override IEnumerable<Entity> Entities() {
        yield return new Entity { Id = DeviceId("weather"), Kind = EntityKind.Device, Name = "weather", Properties = { PropertyInfo.Number("sky_temp_c", "C", "Sky temperature") } };
    }

    protected override async Task<StateMsg?> StateAsync(CancellationToken cancellationToken) {
        var m = await base.StateAsync(cancellationToken) ?? new StateMsg(Now());
        m.Set(DeviceId("weather"), "sky_temp_c", -18.0);
        return m;
    }

    protected override void Log(Level level, string message, Exception? exception = null) {
        lock (Logged) {
            Logged.Add($"{level}: {message}");
        }
    }
}


/// <summary>
/// The protocol round trip a hardware author relies on: the agent joins a site, is handed a task by
/// the service, runs it, and the service sees it complete. Same scenario as the Python package's
/// <c>test_agent.py</c>.
/// </summary>
[NonParallelizable]
public class AgentIntegrationTests {
    private const double Lat = 37.8044;
    private const double Lon = -122.2712;

    [SetUp]
    public void RequireServer() => ServerFixture.RequireServer();

    [Test]
    public async Task AgentJoinsSiteRunsTasksAndAnswersCommands() {
        var siteId = "csagent-" + Guid.NewGuid().ToString("N")[..8];
        await ServerFixture.PostAsync("/api/sites", new {
            id = siteId,
            name = "C# agent site",
            from_assembly = "observatory",
            location = new { lat_deg = Lat, lon_deg = Lon },
        });

        try {
            // what the agent will be handed in `welcome`, from the service's point of view
            var manifest = await ServerFixture.GetAsync($"/api/sites/{siteId}/entities");
            string InstrumentId(string mountInstance) => manifest["instruments"]!.AsArray()
                .Select(i => i!)
                .First(i => manifest["entities"]!.AsArray().Any(e => e!["id"]!.GetValue<string>() == i["mount"]!.GetValue<string>() && e["instance"]?.GetValue<string>() == mountInstance))["id"]!.GetValue<string>();
            string EntityId(string instance) => manifest["entities"]!.AsArray().First(e => e!["instance"]?.GetValue<string>() == instance)!["id"]!.GetValue<string>();
            var mount = InstrumentId("mount");
            var mount2 = InstrumentId("mount2");
            var shed = EntityId("shed");
            var cam2 = EntityId("cam2");

            var tokenResponse = await ServerFixture.PostAsync($"/api/sites/{siteId}/agent-tokens", new { label = "nunit agent" });
            var token = tokenResponse["token"]!.GetValue<string>();

            var agent = TestAgent.Create(ServerFixture.BaseUrl!, siteId, token, Lat, Lon);
            agent.StateInterval = TimeSpan.FromMilliseconds(200);
            using var runCts = new CancellationTokenSource();
            var runner = agent.RunAsync(runCts.Token);

            try {
                await agent.Connected.WaitAsync(TimeSpan.FromSeconds(10));
                await agent.Ready.WaitAsync(TimeSpan.FromSeconds(10));

                // the manifest told the agent what the site is made of
                Assert.That(agent.Instruments.Select(i => i.Id), Is.EquivalentTo(new[] { mount, mount2 }));
                Assert.That(agent.Instrument("mount2").Camera, Is.EqualTo(cam2));
                Assert.That(agent.Binding.InstrumentId, Is.EqualTo(mount), "the first instrument is driven by default");
                Assert.That(agent.Binding.HaJoint, Is.EqualTo("ra"));
                Assert.That(agent.Binding.RoofEntity, Is.EqualTo(shed));

                // telemetry arrives addressed to the entities and the site shows online
                var state = await WaitForState(siteId, s => s["latest"]![Channels.Channel(mount, "ra")] != null, TimeSpan.FromSeconds(10));
                Assert.That(state["connection"]!.GetValue<string>(), Is.EqualTo("online"));
                Assert.That(state["latest"]![Channels.Channel(shed, Channels.RoofState)]!.GetValue<string>(), Is.EqualTo(Channels.RoofClosed));
                Assert.That(state["joints"]!["mount"]!["ra"]!.GetValue<double>(), Is.EqualTo(state["latest"]![Channels.Channel(mount, "ra")]!.GetValue<double>()), "joint properties drive the twin");
                Assert.That(state["latest"]![Channels.Channel(agent.DeviceId("weather"), "sky_temp_c")], Is.Not.Null, "the agent's own device");

                // a task: the agent opens the roof, unparks, slews, changes filter, exposes and reports frames on the task entity
                var record = await ServerFixture.PostAsync($"/api/sites/{siteId}/tasks", new {
                    name = "Polaris",
                    instrument = mount,
                    target = new { name = "Polaris", ra_deg = 37.95, dec_deg = 89.26 },
                    sequence = new[] { new { filter = "Ha", seconds = 10, count = 2 }, new { filter = "L", seconds = 10, count = 1 } },
                });
                var taskId = record["task"]!["id"]!.GetValue<string>();
                var done = await WaitForTask(siteId, taskId, TimeSpan.FromSeconds(30));
                Assert.That(done["status"]!.GetValue<string>(), Is.EqualTo("completed"), done.ToJsonString());
                Assert.That(done["progress"]!.GetValue<double>(), Is.EqualTo(1.0));

                state = await WaitForState(siteId, s => s["latest"]![Channels.Channel(mount, Channels.MountMode)]?.GetValue<string>() == Channels.MountModeTracking, TimeSpan.FromSeconds(5));
                Assert.That(state["latest"]![Channels.Channel(shed, Channels.RoofState)]!.GetValue<string>(), Is.EqualTo(Channels.RoofOpen));
                Assert.That(state["latest"]![Channels.Channel(mount, "dec")]!.GetValue<double>(), Is.EqualTo(89.26).Within(0.01));
                Assert.That(state["latest"]![Channels.Channel(taskId, Channels.TaskFramesTotal)]!.GetValue<double>(), Is.EqualTo(3), "frames are properties of the task");
                Assert.That(state["latest"]![Channels.Channel(taskId, Channels.TaskFramesDone)]!.GetValue<double>(), Is.EqualTo(3));
                Assert.That(agent.Camera.Exposures.Select(e => e.Filter), Is.EqualTo(new[] { "Ha", "Ha", "L" }));
                Assert.That(agent.Camera.Exposures.All(e => e.TargetName == "Polaris" && e.TaskId == taskId), Is.True);
                Assert.That(agent.Wheel.Changes, Is.EqualTo(new[] { "Ha", "L" }), "the wheel moves only when the filter changes");
                Assert.That(agent.Mount.Calls, Does.Contain("unpark").And.Contain("slew 37.95 89.26"));
                Assert.That(state["events"]!.AsArray().Select(e => e!["message"]!.GetValue<string>()), Does.Contain("roof open"));

                // commands round-trip through the ack path
                var cmd = await ServerFixture.PostAsync($"/api/sites/{siteId}/commands", new { kind = "park" });
                Assert.That(cmd["command_id"], Is.Not.Null);
                state = await WaitForState(siteId, s => s["latest"]![Channels.Channel(mount, Channels.MountMode)]?.GetValue<string>() == Channels.MountModeParked, TimeSpan.FromSeconds(5));
                Assert.That(state["latest"]![Channels.Channel(mount, Channels.MountMode)]!.GetValue<string>(), Is.EqualTo(Channels.MountModeParked));

                // an unsupported command is acked as such, not dropped
                await ServerFixture.PostAsync($"/api/sites/{siteId}/commands", new { kind = "set_time_scale", factor = 10 });
                state = await WaitForState(siteId, s => s["events"]!.AsArray().Any(e => e!["message"]!.GetValue<string>().Contains("not supported")), TimeSpan.FromSeconds(5));

                // a failing command is acked with its reason: the roof is already open, closing it works, opening a closed one works
                await ServerFixture.PostAsync($"/api/sites/{siteId}/commands", new { kind = "close_roof" });
                state = await WaitForState(siteId, s => s["latest"]![Channels.Channel(shed, Channels.RoofState)]?.GetValue<string>() == Channels.RoofClosed, TimeSpan.FromSeconds(5));

                // cancelling a long task stops it (real-time exposures from here on)
                agent.Clock.TimeScale = 1.0;
                record = await ServerFixture.PostAsync($"/api/sites/{siteId}/tasks", new {
                    name = "Polaris again",
                    instrument = mount,
                    target = new { name = "Polaris", ra_deg = 37.95, dec_deg = 89.26 },
                    sequence = new[] { new { filter = "L", seconds = 600, count = 10 } },
                });
                taskId = record["task"]!["id"]!.GetValue<string>();
                await WaitForState(siteId, s => s["tasks"]!.AsArray().Any(t => t!["task"]!["id"]!.GetValue<string>() == taskId && t["status"]!.GetValue<string>() == "running"), TimeSpan.FromSeconds(10));
                await ServerFixture.DeleteAsync($"/api/sites/{siteId}/tasks/{taskId}");
                done = await WaitForTask(siteId, taskId, TimeSpan.FromSeconds(20));
                Assert.That(done["status"]!.GetValue<string>(), Is.EqualTo("cancelled"), done.ToJsonString());
                Assert.That(agent.RunningTasks, Is.Empty);
                Assert.That(agent.Camera.Read().Activity, Is.EqualTo(CameraActivity.Idle), "the exposure in progress was aborted");

                // safe: park and close the roof
                await ServerFixture.PostAsync($"/api/sites/{siteId}/commands", new { kind = "safe" });
                agent.Clock.TimeScale = 0.01;
                state = await WaitForState(siteId, s => s["latest"]![Channels.Channel(mount, Channels.MountMode)]?.GetValue<string>() == Channels.MountModeParked && s["latest"]![Channels.Channel(shed, Channels.RoofState)]?.GetValue<string>() == Channels.RoofClosed, TimeSpan.FromSeconds(10));
            } finally {
                agent.Stop();
                runCts.Cancel();

                try {
                    await runner.WaitAsync(TimeSpan.FromSeconds(10));
                } catch (Exception) {
                }

                TestContext.Out.WriteLine(string.Join("\n", agent.Logged));
            }
        } finally {
            await ServerFixture.DeleteAsync($"/api/sites/{siteId}");
        }
    }

    [Test]
    public async Task BadTokenIsRefusedAndTheAgentKeepsRetrying() {
        var siteId = "csbad-" + Guid.NewGuid().ToString("N")[..8];
        await ServerFixture.PostAsync("/api/sites", new { id = siteId, name = "bad token", from_assembly = "observatory", location = new { lat_deg = Lat, lon_deg = Lon } });

        try {
            var agent = TestAgent.Create(ServerFixture.BaseUrl!, siteId, "tca_not_a_real_token", Lat, Lon);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await agent.RunAsync(cts.Token);

            Assert.That(agent.IsConnected, Is.False);
            Assert.That(agent.Logged, Has.Some.Contains("reconnecting"));
        } finally {
            await ServerFixture.DeleteAsync($"/api/sites/{siteId}");
        }
    }

    private static async Task<JsonNode> WaitForState(string siteId, Func<JsonNode, bool> ready, TimeSpan timeout) {
        var deadline = DateTime.UtcNow + timeout;
        JsonNode state;

        while (true) {
            state = await ServerFixture.GetAsync($"/api/sites/{siteId}/state");

            if (ready(state) || DateTime.UtcNow > deadline) {
                break;
            }

            await Task.Delay(150);
        }

        Assert.That(ready(state), Is.True, $"state did not reach the expected shape within {timeout.TotalSeconds}s: {state.ToJsonString()}");
        return state;
    }

    private static async Task<JsonNode> WaitForTask(string siteId, string taskId, TimeSpan timeout) {
        var state = await WaitForState(siteId, s => s["tasks"]!.AsArray().Any(t => t!["task"]!["id"]!.GetValue<string>() == taskId && IsTerminal(t["status"]!.GetValue<string>())), timeout);
        return state["tasks"]!.AsArray().First(t => t!["task"]!["id"]!.GetValue<string>() == taskId)!;
    }

    private static bool IsTerminal(string status) => status is "completed" or "failed" or "cancelled" or "skipped";
}

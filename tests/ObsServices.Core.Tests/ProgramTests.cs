using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using NUnit.Framework;
using ObsServices.Core;
using ObsServices.Core.Agent;
using ObsServices.Core.Protocol;

namespace ObsServices.Core.Tests;

/// <summary>
/// Programs: the service stores a N.I.N.A. sequence per organisation, queues it as a task, and hands
/// the body only to an agent that declared the format. The agent runs it through its sequencer host
/// and publishes itself to the site's `sequencer` component.
/// </summary>
[NonParallelizable]
public class ProgramTests {
    private const double Lat = 37.8044;
    private const double Lon = -122.2712;

    [SetUp]
    public void RequireServer() => ServerFixture.RequireServer();

    /// <summary>The observatory assembly plus a `sequencer` instance, as an inline site document.</summary>
    private static async Task<(string SiteId, string Org)> CreateSiteWithSequencer(string prefix) {
        var me = await ServerFixture.GetAsync("/api/me");
        var org = me["user"]!["personal_org"]!.GetValue<string>();
        var library = Path.Combine(TelescopeServer.Locate().Library!, "assemblies", "observatory.json");
        var assembly = JsonNode.Parse(await File.ReadAllTextAsync(library))!.AsObject();
        assembly["instances"]!["seq"] = new JsonObject { ["component"] = "sequencer" };
        var siteId = prefix + Guid.NewGuid().ToString("N")[..8];
        await ServerFixture.PostAsync("/api/sites", new JsonObject {
            ["id"] = siteId,
            ["name"] = "program site",
            ["assembly"] = assembly,
            ["location"] = new JsonObject { ["lat_deg"] = Lat, ["lon_deg"] = Lon },
        });
        return (siteId, org);
    }

    private static async Task<JsonNode> UploadReferenceSequence(string org) {
        var path = Path.Combine(TelescopeServer.Locate().Library!, "..", "reference_nina_sequences", "M57_ObsServicesTestSequence.json");

        if (!File.Exists(path)) {
            Assert.Ignore($"reference sequence not found at {path}");
        }

        var body = await File.ReadAllTextAsync(path);
        var r = await ServerFixture.Admin.PostAsync($"/api/orgs/{org}/programs?name=M57", new StringContent(body, Encoding.UTF8, "application/json"));
        var text = await r.Content.ReadAsStringAsync();
        Assert.That(r.IsSuccessStatusCode, Is.True, $"upload -> {(int)r.StatusCode}: {text}");
        return JsonNode.Parse(text)!;
    }

    [Test]
    public async Task ProgramTaskRunsThroughTheSequencerHostAndTheSequencerEntityReports() {
        var (siteId, org) = await CreateSiteWithSequencer("csprg-");

        try {
            var program = await UploadReferenceSequence(org);
            Assert.That(program["format"]!.GetValue<string>(), Is.EqualTo("nina.sequence"));
            Assert.That(program["digest"]!["extensions"]!.AsArray().Select(e => e!.GetValue<string>()), Does.Contain("DaleGhent.NINA.AstroPhysicsTools"));
            var programId = program["id"]!.GetValue<string>();
            var manifest = await ServerFixture.GetAsync($"/api/sites/{siteId}/entities");
            var seq = manifest["entities"]!.AsArray().First(e => e!["component"]?.GetValue<string>() == "sequencer")!["id"]!.GetValue<string>();
            var mount = manifest["instruments"]![0]!["id"]!.GetValue<string>();

            // queued before any capable agent is connected: it waits
            var record = await ServerFixture.PostAsync($"/api/sites/{siteId}/tasks", new { program = new { id = programId } });
            var taskId = record["task"]!["id"]!.GetValue<string>();
            Assert.That(record["status"]!.GetValue<string>(), Is.EqualTo("queued"));
            Assert.That(record["message"]!.GetValue<string>(), Does.Contain("waiting for an agent that runs nina.sequence"));
            Assert.That(record["task"]!["sequence"]!.AsArray().Count, Is.GreaterThan(0), "the digest gave it exposures");

            // an agent without the capability connects: the task stays queued
            var token = (await ServerFixture.PostAsync($"/api/sites/{siteId}/agent-tokens", new { label = "plain" }))["token"]!.GetValue<string>();
            var plain = TestAgent.Create(ServerFixture.BaseUrl!, siteId, token, Lat, Lon, new ObservatoryAgentOptions { TelemetryOnly = false });
            plain.StateInterval = TimeSpan.FromMilliseconds(200);
            using (var cts = new CancellationTokenSource()) {
                var run = plain.RunAsync(cts.Token);
                await plain.Ready.WaitAsync(TimeSpan.FromSeconds(10));
                await Task.Delay(600);
                var state = await ServerFixture.GetAsync($"/api/sites/{siteId}/state");
                Assert.That(state["tasks"]!.AsArray().First(t => t!["task"]!["id"]!.GetValue<string>() == taskId)!["status"]!.GetValue<string>(), Is.EqualTo("queued"));
                Assert.That(plain.RunningTasks, Is.Empty, "never handed over");
                plain.Stop();
                cts.Cancel();
                try { await run.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
            }

            // one with a sequencer host declares program:nina.sequence and gets the body on hello
            var agent = TestAgent.Create(ServerFixture.BaseUrl!, siteId, token, Lat, Lon, new ObservatoryAgentOptions { TelemetryOnly = false }, withSequencer: true);
            agent.StateInterval = TimeSpan.FromMilliseconds(200);
            Assert.That(agent.Capabilities, Does.Contain("program:nina.sequence").And.Contain("program:nina.target"));
            using var runCts = new CancellationTokenSource();
            var runner = agent.RunAsync(runCts.Token);

            try {
                await agent.Ready.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(agent.Binding.SequencerEntity, Is.EqualTo(seq));

                var done = await WaitForTask(siteId, taskId, TimeSpan.FromSeconds(30));
                Assert.That(done["status"]!.GetValue<string>(), Is.EqualTo("completed"), done.ToJsonString());
                var runs = agent.Sequencer!.Runs;
                Assert.That(runs, Has.Count.EqualTo(1));
                Assert.That(runs[0].Id, Is.EqualTo(programId));
                Assert.That(runs[0].Sha256, Is.EqualTo(program["sha256"]!.GetValue<string>()));
                Assert.That(runs[0].BodyLength, Is.EqualTo(program["size"]!.GetValue<int>()), "the body arrived byte for byte");
                Assert.That(agent.Mount.Calls, Is.Empty, "a program drives nothing through the device path");

                // frames were counted against the digest's exposures and the sequencer entity reported
                // (the next telemetry sample after the run shows it idle again)
                var state = await WaitFor(() => ServerFixture.GetAsync($"/api/sites/{siteId}/state"), s => s["latest"]![Channels.Channel(seq, Channels.SequencerState)]?.GetValue<string>() == "idle", TimeSpan.FromSeconds(5));
                var frames = state["latest"]![Channels.Channel(taskId, Channels.TaskFramesTotal)]!.GetValue<double>();
                Assert.That(frames, Is.EqualTo(done["task"]!["sequence"]!.AsArray().Sum(e => e!["count"]!.GetValue<int>())));
                Assert.That(state["latest"]![Channels.Channel(taskId, Channels.TaskFramesDone)]!.GetValue<double>(), Is.EqualTo(frames));
                Assert.That(state["latest"]![Channels.Channel(seq, Channels.SequencerSoftware)]!.GetValue<string>(), Is.EqualTo("N.I.N.A."));
                Assert.That(state["latest"]![Channels.Channel(seq, Channels.SequencerState)]!.GetValue<string>(), Is.EqualTo("idle"));
                Assert.That(state["latest"]![Channels.Channel(seq, Channels.SequencerInstrument)]!.GetValue<string>(), Is.EqualTo(mount));
                Assert.That(state["latest"]![Channels.Channel(seq, Channels.SequencerExtensions)]!.GetValue<string>(), Does.Contain("AstroPhysicsTools"));
                Assert.That(state["latest"]![Channels.Channel(seq, Channels.SequencerTelemetryOnly)]!.GetValue<bool>(), Is.False);

                // the sequencer's controls: telemetry_only flips the mode live through a set
                var set = await ServerFixture.PostAsync($"/api/sites/{siteId}/set", new { entity = seq, values = new { telemetry_only = true } });
                var setDone = await WaitFor(() => ServerFixture.GetAsync($"/api/sites/{siteId}/set/{set["id"]!.GetValue<string>()}"), r => r["status"]!.GetValue<string>() is not ("pending" or "accepted"), TimeSpan.FromSeconds(10));
                Assert.That(setDone["status"]!.GetValue<string>(), Is.EqualTo("done"));
                Assert.That(agent.TelemetryOnly, Is.True);
                var again = await ServerFixture.PostAsync($"/api/sites/{siteId}/tasks", new { program = new { id = programId } });
                var skipped = await WaitForTask(siteId, again["task"]!["id"]!.GetValue<string>(), TimeSpan.FromSeconds(10));
                Assert.That(skipped["status"]!.GetValue<string>(), Is.EqualTo("skipped"));

                // and a running program is stopped by setting the sequencer idle
                await ServerFixture.PostAsync($"/api/sites/{siteId}/set", new { entity = seq, values = new { telemetry_only = false } });
                await WaitFor(() => Task.FromResult(JsonNode.Parse("{}")!), _ => !agent.TelemetryOnly, TimeSpan.FromSeconds(5));
                agent.Clock.TimeScale = 1.0;
                var longRun = await ServerFixture.PostAsync($"/api/sites/{siteId}/tasks", new { program = new { id = programId } });
                var longId = longRun["task"]!["id"]!.GetValue<string>();
                await WaitFor(() => ServerFixture.GetAsync($"/api/sites/{siteId}/state"), s => s["latest"]![Channels.Channel(seq, Channels.SequencerState)]?.GetValue<string>() == "running", TimeSpan.FromSeconds(10));
                await ServerFixture.PostAsync($"/api/sites/{siteId}/set", new { entity = seq, values = new { state = "idle" } });
                var cancelled = await WaitForTask(siteId, longId, TimeSpan.FromSeconds(15));
                Assert.That(cancelled["status"]!.GetValue<string>(), Is.EqualTo("cancelled"), cancelled.ToJsonString());
            } finally {
                agent.Stop();
                runCts.Cancel();
                try { await runner.WaitAsync(TimeSpan.FromSeconds(10)); } catch (Exception) { }
                TestContext.Out.WriteLine(string.Join("\n", agent.Logged));
            }
        } finally {
            await ServerFixture.DeleteAsync($"/api/sites/{siteId}");
        }
    }

    private static async Task<JsonNode> WaitFor(Func<Task<JsonNode>> fetch, Func<JsonNode, bool> ready, TimeSpan timeout) {
        var deadline = DateTime.UtcNow + timeout;
        JsonNode node;

        while (true) {
            node = await fetch();

            if (ready(node) || DateTime.UtcNow > deadline) {
                break;
            }

            await Task.Delay(150);
        }

        Assert.That(ready(node), Is.True, $"not ready within {timeout.TotalSeconds}s: {node.ToJsonString()}");
        return node;
    }

    private static async Task<JsonNode> WaitForTask(string siteId, string taskId, TimeSpan timeout) {
        var state = await WaitFor(() => ServerFixture.GetAsync($"/api/sites/{siteId}/state"), s => s["tasks"]!.AsArray().Any(t => t!["task"]!["id"]!.GetValue<string>() == taskId && t["status"]!.GetValue<string>() is "completed" or "failed" or "cancelled" or "skipped"), timeout);
        return state["tasks"]!.AsArray().First(t => t!["task"]!["id"]!.GetValue<string>() == taskId)!;
    }
}

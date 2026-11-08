using System.Text.Json.Nodes;
using NUnit.Framework;
using ObsServices.Core;
using ObsServices.Core.Agent;
using ObsServices.Core.Imaging;
using ObsServices.Core.Protocol;
using ObsServices.Core.Tests.Fakes;

namespace ObsServices.Core.Tests;

/// <summary>
/// Frames end to end against the live service (protocol v6): a task's exposures are announced, granted,
/// uploaded in chunks, verified by the service, released, and dropped from the spool; the record, preview
/// and bytes read back through the API; the <c>uploader</c> component reports the spool; a frame left in
/// the spool is picked up by the next agent through the inventory.
/// </summary>
[NonParallelizable]
public class ImageTests {
    private const double Lat = 37.8044;
    private const double Lon = -122.2712;
    private string spoolDir = string.Empty;

    [SetUp]
    public void SetUp() {
        ServerFixture.RequireServer();
        spoolDir = Path.Combine(Path.GetTempPath(), "obsservices-spool-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void TearDown() {
        if (Directory.Exists(spoolDir)) {
            Directory.Delete(spoolDir, recursive: true);
        }
    }

    /// <summary>The observatory assembly plus an <c>uploader</c> instance.</summary>
    private static async Task<string> CreateSiteWithUploader(string prefix) {
        var library = Path.Combine(TelescopeServer.Locate().Library!, "assemblies", "observatory.json");
        var assembly = JsonNode.Parse(await File.ReadAllTextAsync(library))!.AsObject();
        assembly["instances"]!["spool"] = new JsonObject { ["component"] = "uploader" };
        var siteId = prefix + Guid.NewGuid().ToString("N")[..8];
        await ServerFixture.PostAsync("/api/sites", new JsonObject {
            ["id"] = siteId,
            ["name"] = "image site",
            ["assembly"] = assembly,
            ["location"] = new JsonObject { ["lat_deg"] = Lat, ["lon_deg"] = Lon },
        });
        return siteId;
    }

    private static bool Received(JsonNode record) =>
        record["copies"]!.AsArray().Any(c => c!["state"]!.GetValue<string>() is "received" or "archived" or "vaulted");

    [Test]
    public async Task FramesAreAnnouncedUploadedVerifiedAndReleased() {
        var siteId = await CreateSiteWithUploader("csimg-");

        try {
            var manifest = await ServerFixture.GetAsync($"/api/sites/{siteId}/entities");
            var mount = manifest["instruments"]![0]!["id"]!.GetValue<string>();
            var uploader = manifest["entities"]!.AsArray().First(e => e!["component"]?.GetValue<string>() == Channels.UploaderComponent)!["id"]!.GetValue<string>();
            var token = (await ServerFixture.PostAsync($"/api/sites/{siteId}/agent-tokens", new { label = "img" }))["token"]!.GetValue<string>();

            var agent = TestAgent.Create(ServerFixture.BaseUrl!, siteId, token, Lat, Lon, spoolDir: spoolDir);
            agent.StateInterval = TimeSpan.FromMilliseconds(200);
            agent.Camera.Width = 96;
            agent.Camera.Height = 64;
            using var runCts = new CancellationTokenSource();
            var runner = agent.RunAsync(runCts.Token);

            try {
                await agent.Ready.WaitAsync(TimeSpan.FromSeconds(10));
                Assert.That(agent.CameraEntity, Is.Not.Null, "a camera entity to announce from, bound or declared");

                // two exposures through a task: the camera reads out, the hook announces, the service grants, the agent uploads
                var record = await ServerFixture.PostAsync($"/api/sites/{siteId}/tasks", new {
                    name = "Vega",
                    instrument = mount,
                    target = new { name = "Vega", ra_deg = 279.23, dec_deg = 38.78 },
                    sequence = new[] { new { filter = "L", seconds = 5, count = 1 }, new { filter = "Ha", seconds = 5, count = 1 } },
                });
                var taskId = record["task"]!["id"]!.GetValue<string>();
                var done = await WaitForTask(siteId, taskId, TimeSpan.FromSeconds(30));
                Assert.That(done["status"]!.GetValue<string>(), Is.EqualTo("completed"), done.ToJsonString());
                Assert.That(agent.Camera.Frames, Has.Count.EqualTo(2));

                var images = await WaitFor(async () => {
                    var list = (await ServerFixture.GetAsync($"/api/sites/{siteId}/images")).AsArray();
                    return list.Count == 2 && list.All(r => Received(r!)) ? list : null;
                }, TimeSpan.FromSeconds(20), "both frames received by the service");

                foreach (var (hash, fits, info) in agent.Camera.Frames) {
                    var summary = images.First(r => r!["hash"]!.GetValue<string>() == hash)!;
                    Assert.That(summary["kind"]!.GetValue<string>(), Is.EqualTo("light"));
                    Assert.That(summary["camera"]!.GetValue<string>(), Is.EqualTo(agent.CameraEntity));
                    Assert.That(summary["instrument"]!.GetValue<string>(), Is.EqualTo(mount));
                    Assert.That(summary["task_id"]!.GetValue<string>(), Is.EqualTo(taskId));
                    Assert.That(summary["exposure_s"]!.GetValue<double>(), Is.EqualTo(5));
                    Assert.That(summary["filter"]!.GetValue<string>(), Is.EqualTo(info.Filter));
                    Assert.That((summary["width"]!.GetValue<int>(), summary["height"]!.GetValue<int>(), summary["bitpix"]!.GetValue<int>()), Is.EqualTo((96, 64, 16)));
                    Assert.That(summary["bytes"]!.GetValue<long>(), Is.EqualTo(fits.Length));
                    Assert.That(summary["stats"]!["median"]!.GetValue<double>(), Is.GreaterThan(400).And.LessThan(600), "the synthetic pedestal");
                    Assert.That(summary["file_name"]!.GetValue<string>(), Is.EqualTo(info.FileName));
                    Assert.That(summary["header"], Is.Null, "the listing leaves the header out");

                    // the full record, the preview and the bytes
                    var full = await ServerFixture.GetAsync($"/api/sites/{siteId}/images/{hash}");
                    Assert.That(full["header"]!["OBJECT"]!.GetValue<string>(), Is.EqualTo("Vega"));
                    Assert.That(full["header"]!["FILTER"]!.GetValue<string>(), Is.EqualTo(info.Filter));
                    Assert.That(full["file_sha256"]!.GetValue<string>(), Is.EqualTo(Fits.Hex(System.Security.Cryptography.SHA256.HashData(fits))));

                    var preview = await ServerFixture.Admin.GetAsync($"/api/sites/{siteId}/images/{hash}/preview.png");
                    Assert.That(preview.IsSuccessStatusCode, Is.True, "a preview was announced");
                    var png = await preview.Content.ReadAsByteArrayAsync();
                    Assert.That(Png.Size(png), Is.EqualTo((96, 64)), "a small frame previews at full size");

                    var data = await ServerFixture.Admin.GetAsync($"/api/sites/{siteId}/images/{hash}/data.fits");
                    Assert.That(data.IsSuccessStatusCode, Is.True);
                    Assert.That(await data.Content.ReadAsByteArrayAsync(), Is.EqualTo(fits), "the service holds the bytes the camera produced");
                }

                // the wheel's filter rode along as the wheel entity → name map when the instrument has a wheel
                if (agent.Binding.FilterWheelEntity is { } wheel) {
                    var ha = images.First(r => r!["filter"]!.GetValue<string>() == "Ha")!;
                    Assert.That(ha["filters"]![wheel]!.GetValue<string>(), Is.EqualTo("Ha"));
                }

                // the release emptied the spool and the hook saw it
                await WaitUntil(() => agent.Spool!.Count == 0, TimeSpan.FromSeconds(10), "released frames leave the spool");
                Assert.That(agent.Released, Is.EquivalentTo(agent.Camera.Frames.Select(f => f.Hash)));
                Assert.That(Directory.GetFiles(spoolDir, "*.fits"), Is.Empty, "owned copies are deleted on release");

                // the uploader component reports the spool
                var state = await WaitForState(siteId, s => s["latest"]![Channels.Channel(uploader, Channels.UploaderQueuedFrames)]?.GetValue<double>() == 0, TimeSpan.FromSeconds(5));
                Assert.That(state["latest"]![Channels.Channel(uploader, Channels.UploaderQueuedBytes)]!.GetValue<double>(), Is.Zero);
                Assert.That(state["latest"]![Channels.Channel(uploader, Channels.UploaderRateBps)]!.GetValue<double>(), Is.GreaterThan(0), "bytes moved in the last minute");
                Assert.That(state["latest"]![Channels.Channel(uploader, Channels.UploaderFreeBytes)]!.GetValue<double>(), Is.GreaterThan(0));

                // a frame announced outside any task, through the direct API, with the hash the service computes
                var pixels = FakeCamera.Synthesize(64, 48, 99);
                var loose = Fits.Write(pixels, 64, 48, new[] { new FitsCard("IMAGETYP", "FLAT") });
                var looseHash = await agent.AnnounceFrameAsync(loose, new FrameInfo { Kind = ImageKind.Flat, CapturedAt = Timestamp.Now(), ExposureS = 0.5, Filter = "L" });
                Assert.That(looseHash, Is.EqualTo(Fits.HashOfUInt16(pixels)));
                var looseRecord = await WaitFor(async () => {
                    var r = await ServerFixture.Admin.GetAsync($"/api/sites/{siteId}/images/{looseHash}");
                    return r.IsSuccessStatusCode && Received(JsonNode.Parse(await r.Content.ReadAsStringAsync())!) ? JsonNode.Parse("{}") : null;
                }, TimeSpan.FromSeconds(15), "the loose frame is received too");
                Assert.That(looseRecord, Is.Not.Null);
                var flats = (await ServerFixture.GetAsync($"/api/sites/{siteId}/images?kind=flat")).AsArray();
                Assert.That(flats.Select(r => r!["hash"]!.GetValue<string>()), Is.EqualTo(new[] { looseHash }));
                Assert.That(flats[0]!["task_id"], Is.Null, "no task was running");
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
    public async Task AFrameLeftInTheSpoolIsFetchedThroughTheInventoryOfTheNextAgent() {
        var siteId = await CreateSiteWithUploader("csinv-");

        try {
            var token = (await ServerFixture.PostAsync($"/api/sites/{siteId}/agent-tokens", new { label = "inv" }))["token"]!.GetValue<string>();

            // the first agent announces but never uploads (as if it lost power right after the announce)
            var first = TestAgent.Create(ServerFixture.BaseUrl!, siteId, token, Lat, Lon, spoolDir: spoolDir);
            first.IgnoreGrants = true;
            var pixels = FakeCamera.Synthesize(64, 48, 7);
            var fits = Fits.Write(pixels, 64, 48);
            string hash;

            using (var cts = new CancellationTokenSource()) {
                var run = first.RunAsync(cts.Token);
                await first.Ready.WaitAsync(TimeSpan.FromSeconds(10));
                hash = await first.AnnounceFrameAsync(fits, new FrameInfo { Kind = ImageKind.Light, CapturedAt = Timestamp.Now(), ExposureS = 1 });
                await WaitFor(async () => (await ServerFixture.Admin.GetAsync($"/api/sites/{siteId}/images/{hash}")).IsSuccessStatusCode ? JsonNode.Parse("{}") : null, TimeSpan.FromSeconds(10), "announced");
                await Task.Delay(500);
                first.Stop();
                cts.Cancel();

                try {
                    await run.WaitAsync(TimeSpan.FromSeconds(10));
                } catch (Exception) {
                }
            }

            Assert.That(new FrameSpool(spoolDir).Pending.Select(e => e.Hash), Is.EqualTo(new[] { hash }), "the frame is still in the spool on disk");
            Assert.That(Received(await ServerFixture.GetAsync($"/api/sites/{siteId}/images/{hash}")), Is.False, "and the service does not have it");

            // the next agent over the same directory lists it after welcome; the service grants it again
            var second = TestAgent.Create(ServerFixture.BaseUrl!, siteId, token, Lat, Lon, spoolDir: spoolDir);
            using var runCts = new CancellationTokenSource();
            var runner = second.RunAsync(runCts.Token);

            try {
                await second.Ready.WaitAsync(TimeSpan.FromSeconds(10));
                await WaitFor(async () => Received(await ServerFixture.GetAsync($"/api/sites/{siteId}/images/{hash}")) ? JsonNode.Parse("{}") : null, TimeSpan.FromSeconds(15), "fetched through the inventory");
                await WaitUntil(() => second.Spool!.Count == 0, TimeSpan.FromSeconds(10), "and released");
                var data = await ServerFixture.Admin.GetAsync($"/api/sites/{siteId}/images/{hash}/data.fits");
                Assert.That(await data.Content.ReadAsByteArrayAsync(), Is.EqualTo(fits));
            } finally {
                second.Stop();
                runCts.Cancel();

                try {
                    await runner.WaitAsync(TimeSpan.FromSeconds(10));
                } catch (Exception) {
                }

                TestContext.Out.WriteLine(string.Join("\n", second.Logged));
            }
        } finally {
            await ServerFixture.DeleteAsync($"/api/sites/{siteId}");
        }
    }

    private static async Task<T> WaitFor<T>(Func<Task<T?>> probe, TimeSpan timeout, string what) where T : class {
        var deadline = DateTime.UtcNow + timeout;

        while (true) {
            var result = await probe();

            if (result != null) {
                return result;
            }

            if (DateTime.UtcNow > deadline) {
                Assert.Fail($"not {what} within {timeout.TotalSeconds}s");
            }

            await Task.Delay(200);
        }
    }

    private static async Task WaitUntil(Func<bool> condition, TimeSpan timeout, string what) {
        var deadline = DateTime.UtcNow + timeout;

        while (!condition()) {
            if (DateTime.UtcNow > deadline) {
                Assert.Fail($"not {what} within {timeout.TotalSeconds}s");
            }

            await Task.Delay(100);
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
        var state = await WaitForState(siteId, s => s["tasks"]!.AsArray().Any(t => t!["task"]!["id"]!.GetValue<string>() == taskId && t["status"]!.GetValue<string>() is "completed" or "failed" or "cancelled" or "skipped"), timeout);
        return state["tasks"]!.AsArray().First(t => t!["task"]!["id"]!.GetValue<string>() == taskId)!;
    }
}

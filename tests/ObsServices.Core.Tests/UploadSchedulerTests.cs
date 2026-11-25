using System.Net;
using System.Text;
using NUnit.Framework;
using ObsServices.Core.Imaging;
using ObsServices.Core.Protocol;

namespace ObsServices.Core.Tests;

/// <summary>
/// The service grants one hash per announcement, each grant allowing two lanes. The agent must still run at most
/// two uploads at once for the whole site, oldest frame first, whatever the number of grants.
/// </summary>
public class UploadSchedulerTests {
    private string dir = string.Empty;

    [SetUp]
    public void MakeDir() => dir = Path.Combine(Path.GetTempPath(), "obsservices-sched-" + Guid.NewGuid().ToString("N"));

    [TearDown]
    public void RemoveDir() {
        if (Directory.Exists(dir)) {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Takes chunked uploads for many frames, slowly, and records how many ran at once and in what order they began.</summary>
    private sealed class SlowUploadServer : IDisposable {
        private readonly HttpListener listener = new();
        private readonly Dictionary<string, byte[]> expected;
        private readonly Dictionary<string, MemoryStream> received = new();
        private readonly TimeSpan perChunk;
        private int active;

        public SlowUploadServer(Dictionary<string, byte[]> expected, TimeSpan perChunk) {
            this.expected = expected;
            this.perChunk = perChunk;
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/agent/site/images/";
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string Url { get; }

        public int MaxActive { get; private set; }

        public List<string> Started { get; } = new();

        public HashSet<string> Finished { get; } = new();

        private async Task ServeAsync() {
            while (listener.IsListening) {
                HttpListenerContext ctx;

                try {
                    ctx = await listener.GetContextAsync();
                } catch (Exception) {
                    return;
                }

                _ = Task.Run(() => HandleAsync(ctx));
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx) {
            var req = ctx.Request;
            var res = ctx.Response;
            var hash = req.Url!.AbsolutePath.Split('/').Last();

            if (req.HttpMethod == "HEAD") {
                lock (received) {
                    res.StatusCode = 200;
                    res.Headers["Upload-Offset"] = (received.TryGetValue(hash, out var r) ? r.Length : 0).ToString();
                    res.Headers["Upload-Length"] = expected[hash].Length.ToString();
                }

                res.Close();
                return;
            }

            var now = Interlocked.Increment(ref active);

            lock (Started) {
                MaxActive = Math.Max(MaxActive, now);

                if (!Started.Contains(hash)) {
                    Started.Add(hash);
                }
            }

            try {
                using var body = new MemoryStream();
                await req.InputStream.CopyToAsync(body);
                await Task.Delay(perChunk);
                string reply;
                int status;

                lock (received) {
                    var r = received.TryGetValue(hash, out var cur) ? cur : received[hash] = new MemoryStream();
                    r.Write(body.ToArray());

                    if (r.Length < expected[hash].Length) {
                        (status, reply) = (202, $"{{\"offset\":{r.Length}}}");
                    } else if (r.ToArray().SequenceEqual(expected[hash])) {
                        lock (Started) Finished.Add(hash);
                        (status, reply) = (201, "{\"status\":\"received\"}");
                    } else {
                        (status, reply) = (422, "{\"error\":\"file_sha256_mismatch\",\"offset\":0}");
                    }
                }

                res.StatusCode = status;
                res.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(reply);
                await res.OutputStream.WriteAsync(bytes);
                res.Close();
            } finally {
                Interlocked.Decrement(ref active);
            }
        }

        private static int FreePort() {
            using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            return ((IPEndPoint)l.LocalEndpoint).Port;
        }

        public void Dispose() => listener.Close();
    }

    [Test]
    public async Task OneHashPerGrantStillMeansTwoLanesOldestFirst() {
        var agent = TestAgent.Create("http://127.0.0.1:1", "site", "tca_none", 34.0, -118.0, spoolDir: dir);
        var spool = agent.Spool!;
        agent.UploadRateCapBps = 200_000_000; // generous: exercises the sharing, slows nothing
        agent.UploadSettleDelay = TimeSpan.Zero;

        // six frames, spooled newest first so insertion order and capture order disagree
        var frames = new Dictionary<string, byte[]>();
        var byAge = new List<string>();

        for (var i = 0; i < 6; i++) {
            var rng = new Random(100 + i);
            var pixels = new ushort[96 * 64];

            for (var k = 0; k < pixels.Length; k++) {
                pixels[k] = (ushort)rng.Next(0, 65536);
            }

            var fits = Fits.Write(pixels, 96, 64);
            var described = FrameDescriber.Describe(fits, new FrameInfo { Kind = ImageKind.Light, Camera = "cam", CapturedAt = 1_800_000_000 - i * 60, ExposureS = 1 });
            spool.Add(described, fits);
            frames[described.Hash] = fits;
            byAge.Insert(0, described.Hash);
        }

        using var server = new SlowUploadServer(frames, TimeSpan.FromMilliseconds(150));

        // one grant per frame, as the service sends them, each allowing two lanes
        foreach (var hash in frames.Keys) {
            await agent.InjectAsync(new ImageGrant { Hashes = { hash }, UploadUrl = server.Url, Token = "tcg_t", ExpiresAt = Timestamp.Now() + 3600, MaxConcurrency = 2, MaxChunk = 2880 });
        }

        // the gate opens only now: two lanes were taken by the first two grants, the other four wait in the queue
        agent.Connected.Set();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);

        while (spool.Pending.Count > 0 && DateTime.UtcNow < deadline) {
            Assert.That(agent.Uploading.Count, Is.LessThanOrEqualTo(2), "two lanes for the site, however many grants");
            await Task.Delay(50);
        }

        Assert.That(spool.Pending, Is.Empty, "every frame was uploaded");
        Assert.That(server.Finished, Is.EquivalentTo(frames.Keys));
        Assert.That(server.MaxActive, Is.LessThanOrEqualTo(2), "the server never saw more than two uploads at once");
        Assert.That(server.MaxActive, Is.EqualTo(2), "both lanes were used");
        // the first two grants start as they arrive (nothing older was granted yet); everything that had to wait
        // for a lane went oldest first, whatever order the grants came in
        // (two lanes freeing together start two frames within milliseconds, whose first chunks may reach the server
        // in either order, so the comparison is per pair of lanes)
        var firstTwo = server.Started.Take(2).ToList();
        var waited = byAge.Where(h => !firstTwo.Contains(h)).ToList();
        Assert.That(server.Started.Skip(2).Take(2), Is.EquivalentTo(waited.Take(2)), "the two oldest waiting frames went next");
        Assert.That(server.Started.Skip(4).Take(2), Is.EquivalentTo(waited.Skip(2).Take(2)), "then the two youngest");
        Assert.That(agent.UploadQueue, Is.Empty);
    }
}

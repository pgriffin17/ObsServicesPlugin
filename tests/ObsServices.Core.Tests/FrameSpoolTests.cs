using System.Net;
using System.Text;
using NUnit.Framework;
using ObsServices.Core.Imaging;
using ObsServices.Core.Protocol;

namespace ObsServices.Core.Tests;

/// <summary>The spool on disk and the resumable upload client, without a service.</summary>
public class FrameSpoolTests {
    private string dir = string.Empty;

    [SetUp]
    public void MakeDir() {
        dir = Path.Combine(Path.GetTempPath(), "obsservices-spool-" + Guid.NewGuid().ToString("N"));
    }

    [TearDown]
    public void RemoveDir() {
        if (Directory.Exists(dir)) {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static (ImageCaptured Described, byte[] Fits) Frame(int seed, int width = 16, int height = 8) {
        var rng = new Random(seed);
        var pixels = new ushort[width * height];

        for (var i = 0; i < pixels.Length; i++) {
            pixels[i] = (ushort)rng.Next(0, 65536);
        }

        var fits = Fits.Write(pixels, width, height);
        return (FrameDescriber.Describe(fits, new FrameInfo { Kind = ImageKind.Light, Camera = "cam", CapturedAt = 1700000000 + seed, ExposureS = 1 }), fits);
    }

    [Test]
    public void KeepsCopiesAndSurvivesARestart() {
        var (a, fitsA) = Frame(1);
        var (b, fitsB) = Frame(2);

        var spool = new FrameSpool(dir);
        var entryA = spool.Add(a, fitsA);
        spool.Add(b, fitsB);
        Assert.That(File.Exists(entryA.Path), Is.True);
        Assert.That(File.ReadAllBytes(entryA.Path), Is.EqualTo(fitsA));
        Assert.That(spool.Count, Is.EqualTo(2));
        Assert.That(spool.QueuedBytes, Is.EqualTo(fitsA.Length + fitsB.Length));
        Assert.That(spool.Add(a, fitsA), Is.SameAs(entryA), "the same frame twice is one entry");
        spool.MarkUploaded(a.Hash);
        Assert.That(spool.Pending.Select(e => e.Hash), Is.EqualTo(new[] { b.Hash }));

        // a new process opens the same directory
        var reopened = new FrameSpool(dir);
        Assert.That(reopened.Entries.Select(e => e.Hash), Is.EquivalentTo(new[] { a.Hash, b.Hash }));
        Assert.That(reopened.Get(a.Hash)!.Uploaded, Is.True);
        Assert.That(reopened.Get(b.Hash)!.Uploaded, Is.False);
        Assert.That(reopened.FreeBytes(), Is.Not.Null.And.GreaterThan(0));

        // a file that vanished is forgotten on load
        File.Delete(reopened.Get(b.Hash)!.Path);
        Assert.That(new FrameSpool(dir).Entries.Select(e => e.Hash), Is.EqualTo(new[] { a.Hash }));
    }

    [Test]
    public void ReleaseDeletesOnlyWhatItOwns() {
        var (a, fitsA) = Frame(3);
        var (b, fitsB) = Frame(4);
        var elsewhere = Path.Combine(Path.GetTempPath(), "obsservices-ref-" + Guid.NewGuid().ToString("N") + ".fits");
        File.WriteAllBytes(elsewhere, fitsB);

        try {
            var spool = new FrameSpool(dir);
            var owned = spool.Add(a, fitsA);
            var referenced = spool.AddReference(b, elsewhere);
            Assert.That(referenced.Owned, Is.False);

            Assert.That(spool.Release(a.Hash), Is.Not.Null);
            Assert.That(File.Exists(owned.Path), Is.False, "an owned copy goes with the release");
            Assert.That(spool.Release(b.Hash), Is.Not.Null);
            Assert.That(File.Exists(elsewhere), Is.True, "the host's own file is never touched");
            Assert.That(spool.Release("nope"), Is.Null);
            Assert.That(spool.Count, Is.Zero);

            // with deletion off the copy stays
            spool = new FrameSpool(dir) { DeleteOnRelease = false };
            owned = spool.Add(a, fitsA);
            spool.Release(a.Hash);
            Assert.That(File.Exists(owned.Path), Is.True);
        } finally {
            File.Delete(elsewhere);
        }
    }

    [Test]
    public void TrimsUploadedFramesOverTheCapButNeverUnsentOnes() {
        var frames = Enumerable.Range(10, 4).Select(seed => Frame(seed)).ToList();
        var size = frames[0].Fits.Length;
        var spool = new FrameSpool(dir) { MaxBytes = size * 2, DeleteOnRelease = false };

        foreach (var (d, f) in frames) {
            spool.Add(d, f);
        }

        Assert.That(spool.Count, Is.EqualTo(4), "unsent frames are kept even over the cap");
        spool.MarkUploaded(frames[0].Described.Hash);
        Assert.That(spool.Count, Is.EqualTo(3), "an uploaded frame over the cap is dropped at once");
        Assert.That(File.Exists(Path.Combine(dir, frames[0].Described.Hash + ".fits")), Is.False);
        spool.MarkUploaded(frames[1].Described.Hash);
        spool.MarkUploaded(frames[2].Described.Hash);
        Assert.That(spool.Entries.Select(e => e.Hash), Is.EquivalentTo(new[] { frames[2].Described.Hash, frames[3].Described.Hash }), "the oldest uploaded go first, down to the cap");
    }

    /// <summary>A service that takes chunked uploads, loses the first chunk's offset once, and verifies the file SHA-256.</summary>
    private sealed class FakeUploadServer : IDisposable {
        private readonly HttpListener listener = new();
        private readonly byte[] expected;
        private readonly MemoryStream received = new();
        private readonly CancellationTokenSource cts = new();
        private bool conflictOnce;

        public FakeUploadServer(byte[] expected, bool conflictOnce) {
            this.expected = expected;
            this.conflictOnce = conflictOnce;
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/agent/site/images/";
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string Url { get; }

        public List<string> Requests { get; } = new();

        public long Committed => received.Length;

        public bool Finished { get; private set; }

        private async Task ServeAsync() {
            while (!cts.IsCancellationRequested) {
                HttpListenerContext ctx;

                try {
                    ctx = await listener.GetContextAsync();
                } catch (Exception) {
                    return;
                }

                var req = ctx.Request;
                var res = ctx.Response;
                lock (Requests) Requests.Add($"{req.HttpMethod} {req.Headers["Content-Range"]}");

                if (req.Headers["Authorization"] != "Bearer tcg_test") {
                    res.StatusCode = 401;
                    res.Close();
                    continue;
                }

                if (req.HttpMethod == "HEAD") {
                    res.StatusCode = 200;
                    res.Headers["Upload-Offset"] = received.Length.ToString();
                    res.Headers["Upload-Length"] = expected.Length.ToString();
                    res.Close();
                    continue;
                }

                var range = req.Headers["Content-Range"]!["bytes ".Length..];
                var first = long.Parse(range.Split('-')[0]);
                using var body = new MemoryStream();
                await req.InputStream.CopyToAsync(body);

                if (conflictOnce) {
                    conflictOnce = false;
                    await Json(res, 409, $"{{\"offset\":{received.Length}}}");
                    continue;
                }

                if (first != received.Length) {
                    await Json(res, 409, $"{{\"offset\":{received.Length}}}");
                    continue;
                }

                received.Write(body.ToArray());

                if (received.Length < expected.Length) {
                    await Json(res, 202, $"{{\"offset\":{received.Length}}}");
                } else if (received.ToArray().SequenceEqual(expected)) {
                    Finished = true;
                    await Json(res, 201, "{\"status\":\"received\"}");
                } else {
                    received.SetLength(0);
                    await Json(res, 422, "{\"error\":\"file_sha256_mismatch\",\"offset\":0}");
                }
            }
        }

        private static async Task Json(HttpListenerResponse res, int status, string body) {
            res.StatusCode = status;
            res.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            await res.OutputStream.WriteAsync(bytes);
            res.Close();
        }

        private static int FreePort() {
            using var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
            l.Start();
            return ((IPEndPoint)l.LocalEndpoint).Port;
        }

        public void Dispose() {
            cts.Cancel();
            listener.Close();
        }
    }

    [Test]
    public async Task UploadsInChunksAndResumesAfterAConflict() {
        var (described, fits) = Frame(20, 128, 64);
        Assert.That(fits.Length, Is.GreaterThan(3 * 2880));
        using var server = new FakeUploadServer(fits, conflictOnce: true);
        using var http = new HttpClient();
        var grant = new ImageGrant { Hashes = { described.Hash }, UploadUrl = server.Url, Token = "tcg_test", ExpiresAt = Timestamp.Now() + 3600, MaxConcurrency = 2, MaxChunk = 2880 };
        var chunks = new List<long>();

        using var data = new MemoryStream(fits);
        var result = await FrameUploader.UploadAsync(http, grant, described.Hash, data, chunks.Add);

        Assert.That(result.Ok, Is.True, result.ToString());
        Assert.That(result.Http, Is.EqualTo(201));
        Assert.That(result.Sent, Is.EqualTo(fits.Length));
        Assert.That(chunks.Sum(), Is.EqualTo(fits.Length));
        Assert.That(chunks.All(c => c <= 2880), Is.True, "chunks respect max_chunk");
        Assert.That(server.Finished, Is.True);
        Assert.That(server.Requests[0], Does.StartWith("HEAD"));
        Assert.That(server.Requests.Count(r => r.StartsWith("PATCH")), Is.EqualTo(fits.Length / 2880 + 1), "one PATCH per chunk plus the one the server bounced");

        // a wrong token is a refusal, not a retry
        var bad = new ImageGrant { Hashes = { described.Hash }, UploadUrl = server.Url, Token = "tcg_wrong", ExpiresAt = grant.ExpiresAt, MaxConcurrency = 1, MaxChunk = 2880 };
        data.Position = 0;
        var refused = await FrameUploader.UploadAsync(http, bad, described.Hash, data);
        Assert.That(refused.Status, Is.EqualTo(UploadStatus.Refused));
        Assert.That(refused.Http, Is.EqualTo(401));
    }
}

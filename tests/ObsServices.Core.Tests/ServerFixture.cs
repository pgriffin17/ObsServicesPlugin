using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace ObsServices.Core.Tests;

/// <summary>
/// Starts the telescope_control debug server once for the integration tests: in-memory stores, dev
/// sign-in, no sky upstream, on a free port. Mirrors <c>python/tests/conftest.py</c> in that repo.
/// The binary comes from <c>TELESCOPE_SERVER_BIN</c>, else <c>../telescope_control/target/debug/telescope-server</c>
/// next to this repository; the component library from <c>TELESCOPE_LIBRARY</c>, else the same checkout.
/// </summary>
[SetUpFixture]
public class ServerFixture {
    private static Process? process;
    private static string? logPath;

    public static string? BaseUrl { get; private set; }

    public static string? Unavailable { get; private set; }

    /// <summary>Signed in as the dev admin with a cookie session (can mint tokens and create sites).</summary>
    public static HttpClient Admin { get; private set; } = null!;

    public static void RequireServer() {
        if (BaseUrl == null) {
            Assert.Ignore(Unavailable ?? "server not started");
        }
    }

    [OneTimeSetUp]
    public async Task Start() {
        var repoRoot = FindRepoRoot();
        var sibling = repoRoot == null ? null : Path.GetFullPath(Path.Combine(repoRoot, "..", "telescope_control"));
        var bin = Environment.GetEnvironmentVariable("TELESCOPE_SERVER_BIN");
        var library = Environment.GetEnvironmentVariable("TELESCOPE_LIBRARY");

        if (string.IsNullOrEmpty(bin) && sibling != null) {
            bin = Path.Combine(sibling, "target", "debug", "telescope-server");
        }

        if (string.IsNullOrEmpty(library) && sibling != null) {
            library = Path.Combine(sibling, "library");
        }

        if (string.IsNullOrEmpty(bin) || !File.Exists(bin)) {
            Unavailable = $"telescope-server not found at `{bin}` (cargo build -p telescope-server in telescope_control, or set TELESCOPE_SERVER_BIN)";
            return;
        }

        if (string.IsNullOrEmpty(library) || !Directory.Exists(library)) {
            Unavailable = $"telescope_control library not found at `{library}` (set TELESCOPE_LIBRARY)";
            return;
        }

        var port = FreePort();
        var baseUrl = $"http://127.0.0.1:{port}";
        var tmp = Path.Combine(Path.GetTempPath(), "obsservices-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        logPath = Path.Combine(tmp, "server.log");

        var psi = new ProcessStartInfo(bin) {
            // cwd outside the checkout so its .env is not picked up
            WorkingDirectory = tmp,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--library");
        psi.ArgumentList.Add(library);
        psi.ArgumentList.Add("--static-dir");
        psi.ArgumentList.Add(Path.Combine(tmp, "nostatic"));
        psi.Environment["TELESCOPE_STORE"] = "memory:";
        psi.Environment["TELESCOPE_SERIES_STORE"] = "memory:";
        psi.Environment["TELESCOPE_DEV_LOGIN"] = "1";
        psi.Environment["TELESCOPE_PORT"] = port.ToString();
        psi.Environment["TELESCOPE_PUBLIC_URL"] = baseUrl;
        psi.Environment["TELESCOPE_SKY_DIR"] = Path.Combine(tmp, "sky");
        psi.Environment["TELESCOPE_SKY_UPSTREAM"] = "0";
        psi.Environment["TELESCOPE_SKY_MIRROR"] = "off";
        psi.Environment["TELESCOPE_ADMINS"] = "admin@test.local";
        psi.Environment["RUST_LOG"] = "warn";
        psi.Environment.Remove("TELESCOPE_BIND");

        var log = new StreamWriter(logPath) { AutoFlush = true };
        process = Process.Start(psi) ?? throw new InvalidOperationException("could not start telescope-server");
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);

        while (true) {
            if (process.HasExited) {
                throw new InvalidOperationException($"telescope-server exited with {process.ExitCode}; see {logPath}");
            }

            try {
                var r = await probe.GetAsync(baseUrl + "/api/health");

                if (r.IsSuccessStatusCode) {
                    break;
                }
            } catch (HttpRequestException) {
            } catch (TaskCanceledException) {
            }

            if (DateTime.UtcNow > deadline) {
                throw new TimeoutException($"telescope-server did not come up; see {logPath}");
            }

            await Task.Delay(300);
        }

        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        Admin = new HttpClient(handler) { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromSeconds(30) };
        var login = await Admin.PostAsJsonAsync("/auth/dev-login", new { email = "admin@test.local" });
        login.EnsureSuccessStatusCode();
        BaseUrl = baseUrl;
    }

    [OneTimeTearDown]
    public void Stop() {
        Admin?.Dispose();

        if (process is { HasExited: false }) {
            try {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            } catch (InvalidOperationException) {
            }
        }

        process?.Dispose();
    }

    // ---- API helpers used by the tests --------------------------------------------------------------

    public static async Task<JsonNode> PostAsync(string path, object body) {
        var r = await Admin.PostAsJsonAsync(path, body);
        var text = await r.Content.ReadAsStringAsync();
        Assert.That(r.IsSuccessStatusCode, Is.True, $"POST {path} -> {(int)r.StatusCode}: {text}");
        return JsonNode.Parse(text) ?? new JsonObject();
    }

    public static async Task<JsonNode> GetAsync(string path) {
        var r = await Admin.GetAsync(path);
        var text = await r.Content.ReadAsStringAsync();
        Assert.That(r.IsSuccessStatusCode, Is.True, $"GET {path} -> {(int)r.StatusCode}: {text}");
        return JsonNode.Parse(text) ?? new JsonObject();
    }

    public static async Task DeleteAsync(string path) {
        var r = await Admin.DeleteAsync(path);
        Assert.That(r.IsSuccessStatusCode, Is.True, $"DELETE {path} -> {(int)r.StatusCode}");
    }

    private static string? FindRepoRoot() {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        while (dir != null) {
            if (File.Exists(Path.Combine(dir.FullName, "ObsServicesPlugin.slnx"))) {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }

    private static int FreePort() {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

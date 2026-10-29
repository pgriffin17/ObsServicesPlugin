using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;

namespace ObsServices.Core.Tests;

/// <summary>
/// One telescope_control server process on a free port, with dev sign-in and no sky upstream.
/// The document store is in memory unless a sqlite path is asked for, which lets a test kill and
/// restart the process while its sites and tokens survive.
/// </summary>
public sealed class TelescopeServer : IDisposable {
    private readonly string bin;
    private readonly string library;
    private readonly string tmp;
    private readonly string store;
    private Process? process;

    public TelescopeServer(string bin, string library, bool persistent) {
        this.bin = bin;
        this.library = library;
        tmp = Path.Combine(Path.GetTempPath(), "obsservices-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        Port = FreePort();
        BaseUrl = $"http://127.0.0.1:{Port}";
        LogPath = Path.Combine(tmp, "server.log");
        store = persistent ? "sqlite:" + Path.Combine(tmp, "telescope.db") : "memory:";
    }

    public int Port { get; }

    public string BaseUrl { get; }

    public string LogPath { get; }

    /// <summary>Where the binary and library are: <c>TELESCOPE_SERVER_BIN</c> / <c>TELESCOPE_LIBRARY</c>, else the sibling checkout.</summary>
    public static (string? Bin, string? Library, string? Problem) Locate() {
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
            return (null, null, $"telescope-server not found at `{bin}` (cargo build -p telescope-server in telescope_control, or set TELESCOPE_SERVER_BIN)");
        }

        if (string.IsNullOrEmpty(library) || !Directory.Exists(library)) {
            return (null, null, $"telescope_control library not found at `{library}` (set TELESCOPE_LIBRARY)");
        }

        return (bin, library, null);
    }

    public async Task StartAsync() {
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
        psi.Environment["TELESCOPE_STORE"] = store;
        psi.Environment["TELESCOPE_SERIES_STORE"] = store;
        psi.Environment["TELESCOPE_DEV_LOGIN"] = "1";
        psi.Environment["TELESCOPE_PORT"] = Port.ToString();
        psi.Environment["TELESCOPE_PUBLIC_URL"] = BaseUrl;
        psi.Environment["TELESCOPE_SKY_DIR"] = Path.Combine(tmp, "sky");
        psi.Environment["TELESCOPE_SKY_UPSTREAM"] = "0";
        psi.Environment["TELESCOPE_SKY_MIRROR"] = "off";
        psi.Environment["TELESCOPE_ADMINS"] = "admin@test.local";
        psi.Environment["RUST_LOG"] = "warn";
        psi.Environment.Remove("TELESCOPE_BIND");

        var log = new StreamWriter(LogPath, append: true) { AutoFlush = true };
        process = Process.Start(psi) ?? throw new InvalidOperationException("could not start telescope-server");
        process.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(90);

        while (true) {
            if (process.HasExited) {
                throw new InvalidOperationException($"telescope-server exited with {process.ExitCode}; see {LogPath}");
            }

            try {
                var r = await probe.GetAsync(BaseUrl + "/api/health");

                if (r.IsSuccessStatusCode) {
                    return;
                }
            } catch (HttpRequestException) {
            } catch (TaskCanceledException) {
            }

            if (DateTime.UtcNow > deadline) {
                throw new TimeoutException($"telescope-server did not come up; see {LogPath}");
            }

            await Task.Delay(300);
        }
    }

    /// <summary>Kill the process outright, as a crash or a power cut would.</summary>
    public void Kill() {
        if (process is { HasExited: false }) {
            try {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10_000);
            } catch (InvalidOperationException) {
            }
        }

        process?.Dispose();
        process = null;
    }

    /// <summary>Signed in as the dev admin with a cookie session.</summary>
    public async Task<HttpClient> AdminClientAsync() {
        var handler = new HttpClientHandler { CookieContainer = new CookieContainer(), UseCookies = true };
        var client = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromSeconds(30) };
        var login = await client.PostAsJsonAsync("/auth/dev-login", new { email = "admin@test.local" });
        login.EnsureSuccessStatusCode();
        return client;
    }

    public void Dispose() => Kill();

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

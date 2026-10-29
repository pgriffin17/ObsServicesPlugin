using System.Net.Http.Json;
using System.Text.Json.Nodes;
using NUnit.Framework;

namespace ObsServices.Core.Tests;

/// <summary>
/// Starts one telescope_control server for the integration tests (in-memory stores). Mirrors
/// <c>python/tests/conftest.py</c> in that repo. Tests that need their own server, to kill it,
/// create a <see cref="TelescopeServer"/> themselves.
/// </summary>
[SetUpFixture]
public class ServerFixture {
    private static TelescopeServer? server;

    public static string? BaseUrl => server?.BaseUrl;

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
        var (bin, library, problem) = TelescopeServer.Locate();

        if (problem != null) {
            Unavailable = problem;
            return;
        }

        var s = new TelescopeServer(bin!, library!, persistent: false);
        await s.StartAsync();
        Admin = await s.AdminClientAsync();
        server = s;
    }

    [OneTimeTearDown]
    public void Stop() {
        Admin?.Dispose();
        server?.Dispose();
        server = null;
    }

    // ---- API helpers used by the tests --------------------------------------------------------------

    public static Task<JsonNode> PostAsync(string path, object body) => PostAsync(Admin, path, body);

    public static Task<JsonNode> GetAsync(string path) => GetAsync(Admin, path);

    public static Task DeleteAsync(string path) => DeleteAsync(Admin, path);

    public static async Task<JsonNode> PostAsync(HttpClient client, string path, object body) {
        var r = await client.PostAsJsonAsync(path, body);
        var text = await r.Content.ReadAsStringAsync();
        Assert.That(r.IsSuccessStatusCode, Is.True, $"POST {path} -> {(int)r.StatusCode}: {text}");
        return JsonNode.Parse(text) ?? new JsonObject();
    }

    public static async Task<JsonNode> GetAsync(HttpClient client, string path) {
        var r = await client.GetAsync(path);
        var text = await r.Content.ReadAsStringAsync();
        Assert.That(r.IsSuccessStatusCode, Is.True, $"GET {path} -> {(int)r.StatusCode}: {text}");
        return JsonNode.Parse(text) ?? new JsonObject();
    }

    public static async Task DeleteAsync(HttpClient client, string path) {
        var r = await client.DeleteAsync(path);
        Assert.That(r.IsSuccessStatusCode, Is.True, $"DELETE {path} -> {(int)r.StatusCode}");
    }
}

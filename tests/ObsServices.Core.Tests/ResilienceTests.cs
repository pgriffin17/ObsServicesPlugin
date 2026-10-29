using System.Text.Json.Nodes;
using NUnit.Framework;
using ObsServices.Core.Agent;
using ObsServices.Core.Protocol;

namespace ObsServices.Core.Tests;

/// <summary>What happens to the agent when the service goes away and comes back.</summary>
[NonParallelizable]
public class ResilienceTests {
    private const double Lat = 37.8044;
    private const double Lon = -122.2712;

    [Test]
    public async Task SurvivesAServerRestartAndReplaysWhatItBuffered() {
        var (bin, library, problem) = TelescopeServer.Locate();

        if (problem != null) {
            Assert.Ignore(problem);
        }

        // a server of this test's own, on disk, so the site and token outlive the kill
        using var server = new TelescopeServer(bin!, library!, persistent: true);
        await server.StartAsync();
        var admin = await server.AdminClientAsync();
        var siteId = "csres-" + Guid.NewGuid().ToString("N")[..8];
        await ServerFixture.PostAsync(admin, "/api/sites", new { id = siteId, name = "resilience", from_assembly = "observatory", location = new { lat_deg = Lat, lon_deg = Lon } });
        var manifest = await ServerFixture.GetAsync(admin, $"/api/sites/{siteId}/entities");
        var mount = manifest["instruments"]![0]!["id"]!.GetValue<string>();
        var token = (await ServerFixture.PostAsync(admin, $"/api/sites/{siteId}/agent-tokens", new { label = "res" }))["token"]!.GetValue<string>();

        var agent = TestAgent.Create(server.BaseUrl, siteId, token, Lat, Lon);
        agent.StateInterval = TimeSpan.FromMilliseconds(100);
        agent.ReconnectMax = TimeSpan.FromSeconds(2);
        using var runCts = new CancellationTokenSource();
        var runner = agent.RunAsync(runCts.Token);

        try {
            await agent.Ready.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.Delay(400);
            Assert.That(agent.LastError, Is.Null);
            Assert.That(agent.ConsecutiveFailures, Is.Zero);

            // the service dies
            var outageStart = Timestamp.Now();
            server.Kill();
            await WaitUntil(() => !agent.IsConnected, TimeSpan.FromSeconds(10), "the agent notices the socket is gone");
            await Task.Delay(1500);
            Assert.That(agent.Logged, Has.Some.Contains("reconnecting"));
            Assert.That(agent.ConsecutiveFailures, Is.GreaterThan(0));
            Assert.That(agent.LastError, Is.Not.Null);
            Assert.That(agent.IsConnected, Is.False, "nothing to connect to yet");
            var outageEnd = Timestamp.Now();

            // ... and comes back; the backoff is capped at 2 s here, so this takes a few seconds at most
            await server.StartAsync();
            var reconnectStarted = DateTime.UtcNow;
            await agent.Connected.WaitAsync(TimeSpan.FromSeconds(15));
            await agent.Ready.WaitAsync(TimeSpan.FromSeconds(10));
            var reconnectTook = DateTime.UtcNow - reconnectStarted;
            Assert.That(agent.ConsecutiveFailures, Is.Zero, "a successful connect resets the failure count");
            Assert.That(agent.LastError, Is.Null);
            TestContext.Out.WriteLine($"reconnected {reconnectTook.TotalSeconds:0.0}s after the server was back");

            // the state published during the outage arrived with its original timestamps
            admin = await server.AdminClientAsync();
            await WaitUntil(async () => {
                var samples = (await ServerFixture.GetAsync(admin, $"/api/sites/{siteId}/telemetry")).AsArray();
                return samples.Any(s => s!["t"]!.GetValue<double>() > outageStart && s["t"]!.GetValue<double>() < outageEnd && s["state"]![mount] != null);
            }, TimeSpan.FromSeconds(10), "replayed samples from the outage window show up in the site's telemetry");
            var state = await ServerFixture.GetAsync(admin, $"/api/sites/{siteId}/state");
            Assert.That(state["connection"]!.GetValue<string>(), Is.EqualTo("online"));
        } finally {
            agent.Stop();
            runCts.Cancel();

            try {
                await runner.WaitAsync(TimeSpan.FromSeconds(10));
            } catch (Exception) {
            }

            TestContext.Out.WriteLine(string.Join("\n", agent.Logged));
        }
    }

    [Test]
    public async Task HandshakeTimeoutIsEnforced() {
        // 10.255.255.1 is a black hole on most networks: the SYN is never answered (or refused fast, which also passes)
        var agent = TestAgent.Create("http://10.255.255.1:7421", "site", "tca_x", Lat, Lon);
        agent.ConnectTimeout = TimeSpan.FromMilliseconds(500);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));

        await agent.RunAsync(cts.Token);

        Assert.That(agent.ConsecutiveFailures, Is.GreaterThan(0));
        Assert.That(agent.Logged, Has.Some.Contains("reconnecting"));
    }

    [Test]
    public async Task RefusedTokenIsNamedInTheError() {
        ServerFixture.RequireServer();
        var siteId = "csrej-" + Guid.NewGuid().ToString("N")[..8];
        await ServerFixture.PostAsync("/api/sites", new { id = siteId, name = "rejected", from_assembly = "observatory", location = new { lat_deg = Lat, lon_deg = Lon } });

        try {
            var agent = TestAgent.Create(ServerFixture.BaseUrl!, siteId, "tca_not_a_real_token", Lat, Lon);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await agent.RunAsync(cts.Token);

            Assert.That(agent.LastError, Does.Contain("401").And.Contain("token"));
        } finally {
            await ServerFixture.DeleteAsync($"/api/sites/{siteId}");
        }
    }

    private static Task WaitUntil(Func<bool> condition, TimeSpan timeout, string what) => WaitUntil(() => Task.FromResult(condition()), timeout, what);

    private static async Task WaitUntil(Func<Task<bool>> condition, TimeSpan timeout, string what) {
        var deadline = DateTime.UtcNow + timeout;

        while (!await condition()) {
            if (DateTime.UtcNow > deadline) {
                Assert.Fail($"timed out after {timeout.TotalSeconds:0}s waiting until {what}");
            }

            await Task.Delay(100);
        }
    }
}

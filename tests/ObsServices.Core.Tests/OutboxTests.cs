using NUnit.Framework;
using ObsServices.Core.Agent;
using ObsServices.Core.Protocol;

namespace ObsServices.Core.Tests;

/// <summary>What leaves first when the link comes back: outcomes, acks and events, then the freshest sample, then the backlog oldest first.</summary>
public class OutboxTests {
    [Test]
    public async Task UrgentThenFreshestThenBacklogAndOneOutcomePerTask() {
        var agent = TestAgent.Create("http://127.0.0.1:1", "site", "tca_none", 34.0, -118.0, new ObservatoryAgentOptions { TelemetryOnly = true });
        agent.Ready.Set(); // no server: tasks are judged as soon as they arrive

        // a backlog of samples builds while the link is down
        var samples = Enumerable.Range(0, 5).Select(i => new StateMsg(1000 + i)).ToList();

        foreach (var s in samples) {
            s.Set("mount", "ra", s.T);
            agent.EnqueueTelemetryForTest(s);
        }

        // a task arrives twice more after it ended (telemetry-only: skipped at once)
        var task = new ObservationTask { Id = "t1", Name = "t1", Target = new Target { Name = "x" }, Sequence = { new Exposure { Seconds = 1 } } };
        await agent.InjectAsync(new TaskAssign { Task = task });

        for (var waited = 0; agent.FinishedTask("t1") == null && waited < 100; waited++) {
            await Task.Delay(20);
        }

        Assert.That(agent.FinishedTask("t1")?.Status, Is.EqualTo(ObservationStatus.Skipped));
        await agent.InjectAsync(new TaskAssign { Task = task });
        await agent.InjectAsync(new TaskAssign { Task = task });
        await agent.EventAsync("hello there");

        var order = new List<Up>();

        while (agent.TakeOutbound() is { } next) {
            order.Add(next);
        }

        // urgent lane first: the original skipped report, one (not two) re-published outcome, the event
        Assert.That(order[0], Is.TypeOf<StateMsg>());
        Assert.That(((StateMsg)order[0]).State["t1"][Channels.TaskStatus].AsText(), Is.EqualTo("skipped"));
        Assert.That(order[1], Is.TypeOf<StateMsg>());
        Assert.That(((StateMsg)order[1]).State["t1"][Channels.TaskStatus].AsText(), Is.EqualTo("skipped"));
        Assert.That(order[2], Is.TypeOf<Event>());
        Assert.That(((Event)order[2]).Message, Is.EqualTo("hello there"));

        // then the freshest sample, then the backlog oldest first
        var rest = order.Skip(3).Cast<StateMsg>().Select(m => m.T).ToList();
        Assert.That(rest, Is.EqualTo(new double[] { 1004, 1000, 1001, 1002, 1003 }));
        Assert.That(agent.Pending, Is.Zero);
    }
}

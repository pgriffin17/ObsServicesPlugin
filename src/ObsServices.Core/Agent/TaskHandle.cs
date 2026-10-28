using ObsServices.Core.Protocol;

namespace ObsServices.Core.Agent;

/// <summary>
/// What <see cref="SiteAgent.RunTaskAsync"/> gets: the task plus progress reporting. The task is an
/// entity, so progress and frames are property writes addressed to it.
/// </summary>
public sealed class TaskHandle {
    private readonly SiteAgent agent;
    internal readonly CancellationTokenSource Cancellation;

    internal TaskHandle(ObservationTask task, SiteAgent agent, CancellationToken agentStopping) {
        Task = task;
        this.agent = agent;
        Cancellation = CancellationTokenSource.CreateLinkedTokenSource(agentStopping);
    }

    public ObservationTask Task { get; }

    /// <summary>Fires when the service cancels the task or the agent stops. Honour it promptly.</summary>
    public CancellationToken Cancelled => Cancellation.Token;

    public bool IsCancelled => Cancellation.IsCancellationRequested;

    /// <summary>Report progress (0..1 of the planned exposure time) and optionally what is happening.</summary>
    public Task ProgressAsync(double fraction, string? message = null) {
        var m = new StateMsg(agent.Now());
        m.Set(Task.Id, Channels.TaskStatus, ObservationStatus.Running.AsString());
        m.Set(Task.Id, Channels.TaskProgress, Math.Clamp(fraction, 0.0, 1.0));

        if (message != null) {
            m.Set(Task.Id, Channels.TaskMessage, message);
        }

        return agent.PublishAsync(m);
    }

    /// <summary>Frames taken / planned, drawn next to the field on the dome.</summary>
    public Task FramesAsync(int done, int total) {
        var m = new StateMsg(agent.Now());
        m.Set(Task.Id, Channels.TaskFramesDone, done);
        m.Set(Task.Id, Channels.TaskFramesTotal, total);
        return agent.PublishAsync(m);
    }

    public Task EventAsync(string message, Level level = Level.Info) => agent.EventAsync(message, level);
}

namespace ObsServices.Core.Agent;

/// <summary>A manual-reset event that can be awaited (asyncio.Event in C#).</summary>
public sealed class AsyncSignal {
    private TaskCompletionSource source = NewSource();

    public bool IsSet => Volatile.Read(ref source).Task.IsCompleted;

    public void Set() => Volatile.Read(ref source).TrySetResult();

    public void Reset() {
        while (true) {
            var current = Volatile.Read(ref source);

            if (!current.Task.IsCompleted || Interlocked.CompareExchange(ref source, NewSource(), current) == current) {
                return;
            }
        }
    }

    public Task WaitAsync(CancellationToken cancellationToken = default) => Volatile.Read(ref source).Task.WaitAsync(cancellationToken);

    public Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken = default) => Volatile.Read(ref source).Task.WaitAsync(timeout, cancellationToken);

    private static TaskCompletionSource NewSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

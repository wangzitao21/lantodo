namespace LanTodo.Core;

// UI callers capture values before enqueueing. A failed operation must not prevent
// later drafts/actions from being saved, and shutdown waits for all accepted work.
public sealed class LocalWriteQueue : IAsyncDisposable
{
    private readonly object gate = new();
    private Task tail = Task.CompletedTask;
    private bool stopped;

    public Task<T> Enqueue<T>(Func<T> action)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(stopped, this);
            var previous = tail;
            var work = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                return action();
            });
            tail = Observe(work);
            return work;
        }
    }
    public Task Enqueue(Action action) => Enqueue(() => { action(); return true; });
    private static async Task Observe(Task work)
    {
        try { await work.ConfigureAwait(false); }
        catch { /* The original task reports its failure to the caller. */ }
    }
    public Task DrainAsync() { lock (gate) return tail; }
    public async ValueTask DisposeAsync()
    {
        Task pending;
        lock (gate) { stopped = true; pending = tail; }
        await pending.ConfigureAwait(false);
    }
}

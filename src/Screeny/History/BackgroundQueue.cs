namespace Screeny.History;

/// <summary>Runs independent blocking operations sequentially off the UI thread.</summary>
public sealed class BackgroundQueue
{
    private readonly object _gate = new();
    private Task _tail = Task.CompletedTask;

    public Task<T> Enqueue<T>(Func<T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            var task = _tail.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            _tail = task;
            return task;
        }
    }

    /// <summary>Waits for all tasks queued before this call, even if the last one failed.</summary>
    public Task DrainAsync()
    {
        lock (_gate)
            return _tail.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
}

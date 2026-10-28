namespace Screeny.History;

/// <summary>Последовательно выполняет независимые блокирующие операции вне UI-потока.</summary>
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

    /// <summary>Ждёт все задачи, принятые до вызова, даже если последняя завершилась ошибкой.</summary>
    public Task DrainAsync()
    {
        lock (_gate)
            return _tail.ContinueWith(_ => { }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }
}

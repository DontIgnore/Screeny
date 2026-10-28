using Screeny.History;

namespace Screeny.Tests.History;

public class BackgroundQueueTests
{
    [Fact]
    public async Task Jobs_RunInSubmissionOrder_AndFailureDoesNotStopTheQueue()
    {
        var queue = new BackgroundQueue();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new List<int>();
        var first = queue.Enqueue(() => { started.SetResult(); release.Task.GetAwaiter().GetResult(); order.Add(1); return 1; });
        await started.Task;
        var failing = queue.Enqueue<int>(() => { order.Add(2); throw new IOException("test"); });
        var last = queue.Enqueue(() => { order.Add(3); return 3; });
        Assert.False(last.IsCompleted);
        release.SetResult();
        Assert.Equal(1, await first);
        await Assert.ThrowsAsync<IOException>(async () => await failing);
        Assert.Equal(3, await last);
        Assert.Equal(new[] { 1, 2, 3 }, order);
        await queue.DrainAsync();
    }

    [Fact]
    public async Task Drain_WaitsForAcceptedWork()
    {
        var queue = new BackgroundQueue();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var job = queue.Enqueue(() => { release.Task.GetAwaiter().GetResult(); return 42; });
        var drain = queue.DrainAsync();
        Assert.False(drain.IsCompleted);
        release.SetResult();
        await drain;
        Assert.Equal(42, await job);
    }

    [Fact]
    public void Enqueue_RunsWorkOffTheCallingThread()
    {
        int caller = 0, worker = 0;
        var thread = new Thread(() =>
        {
            caller = Environment.CurrentManagedThreadId;
            worker = new BackgroundQueue().Enqueue(() => Environment.CurrentManagedThreadId).GetAwaiter().GetResult();
        });
        thread.Start();
        thread.Join();
        Assert.NotEqual(caller, worker);
    }

    [Fact]
    public async Task Drain_CompletesAfterAFailedJob()
    {
        var queue = new BackgroundQueue();
        var job = queue.Enqueue<int>(() => throw new IOException("test"));
        await Assert.ThrowsAsync<IOException>(async () => await job);
        await queue.DrainAsync();
    }
}

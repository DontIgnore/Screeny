using Screeny.Updates;

namespace Screeny.Tests.Updates;

public class UpdateServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScreenyTests", Guid.NewGuid().ToString("N"));
    private UpdateStateStore Store => new(Path.Combine(_dir, "update-state.json"));

    private sealed class FakeClient : IUpdateClient
    {
        public bool CanUpdate { get; set; } = true;
        public string? CurrentVersion { get; set; } = "0.2.0";
        public UpdateCandidate? Next { get; set; }
        public Exception? Error { get; set; }
        public int Checks { get; private set; }

        public Task<UpdateCandidate?> CheckAsync(CancellationToken ct)
        {
            Checks++;
            return Error is not null ? Task.FromException<UpdateCandidate?>(Error) : Task.FromResult(Next);
        }

        /// <summary>Download waits for this task (null — completes immediately).</summary>
        public Func<UpdateCandidate, Task>? Download { get; set; }
        public List<string> Downloads { get; } = [];
        public int Running { get; private set; }
        public int MaxRunning { get; private set; }

        public async Task DownloadAsync(UpdateCandidate update, Action<int>? progress, CancellationToken ct)
        {
            Downloads.Add(update.Version);
            Running++;
            MaxRunning = Math.Max(MaxRunning, Running);
            try { if (Download is not null) await Download(update); }
            finally { Running--; }
        }
        public void ApplyAfterExit(UpdateCandidate update, string[] restartArgs) { }
        public void ApplySilentlyAfterExit(UpdateCandidate update) { }
    }

    [Theory]
    [InlineData(false, false, true, true)]   // normal installed copy launch
    [InlineData(false, false, false, false)] // publish\Screeny.exe, bin: non-updatable
    [InlineData(true, false, true, false)]   // dev without source — never network
    [InlineData(true, true, true, true)]     // dev with --update-source
    public void AutomaticAllowed(bool dev, bool localSource, bool canUpdate, bool expected)
        => Assert.Equal(expected, UpdateService.AutomaticAllowed(dev, localSource, canUpdate));

    [Theory]
    [InlineData(false, null, UpdateSourceKind.Github)]
    [InlineData(true, null, UpdateSourceKind.None)]               // dev without folder: neither background nor manual check to network
    [InlineData(true, @"C:\rel", UpdateSourceKind.Folder)]
    public void ChooseSource(bool dev, string? folder, UpdateSourceKind expected)
        => Assert.Equal(expected, UpdateService.ChooseSource(dev, folder));

    [Fact]
    public async Task NoSource_ManualCheckIsNotUpdatable()
    {
        using var service = new UpdateService(NoUpdateClient.Instance, Store, () => true);
        Assert.IsType<UpdateCheckResult.NotUpdatable>(await service.CheckAsync(UpdateCheckKind.Manual));
    }

    [Fact]
    public async Task NotUpdatableCopy_NeverAsksSource()
    {
        var client = new FakeClient { CanUpdate = false, Next = new("0.2.1", null) };
        using var service = new UpdateService(client, Store, () => true);
        Assert.IsType<UpdateCheckResult.NotUpdatable>(await service.CheckAsync(UpdateCheckKind.Manual));
        Assert.Equal(0, client.Checks);
    }

    [Fact]
    public async Task UpToDate_ReportsCurrentVersion()
    {
        using var service = new UpdateService(new FakeClient(), Store, () => true);
        var result = Assert.IsType<UpdateCheckResult.UpToDate>(await service.CheckAsync(UpdateCheckKind.Manual));
        Assert.Equal("0.2.0", result.CurrentVersion);
    }

    [Fact]
    public async Task SkippedVersion_SilencesOnlyAutomaticCheck()
    {
        var client = new FakeClient { Next = new("0.2.1", "notes") };
        using var service = new UpdateService(client, Store, () => true);
        Assert.True(service.Skip("0.2.1"));
        Assert.IsType<UpdateCheckResult.Skipped>(await service.CheckAsync(UpdateCheckKind.Automatic));
        Assert.IsType<UpdateCheckResult.Available>(await service.CheckAsync(UpdateCheckKind.Manual));

        client.Next = new("0.2.2", null); // next version shows again
        Assert.IsType<UpdateCheckResult.Available>(await service.CheckAsync(UpdateCheckKind.Automatic));
    }

    [Fact]
    public async Task NetworkError_IsResultNotException()
    {
        var client = new FakeClient { Error = new System.Net.Http.HttpRequestException("offline") };
        using var service = new UpdateService(client, Store, () => true);
        var failed = Assert.IsType<UpdateCheckResult.Failed>(await service.CheckAsync(UpdateCheckKind.Automatic));
        Assert.Equal("offline", failed.Error.Message);
    }

    [Fact]
    public async Task Loop_WaitsStartupDelay_ThenDaily_AndReadsSettingEachTime()
    {
        var client = new FakeClient { Next = new("0.2.1", null) };
        bool enabled = false;
        var delays = new List<TimeSpan>();
        UpdateService? service = null;
        var found = new List<string>();
        Task Delay(TimeSpan span, CancellationToken ct)
        {
            delays.Add(span);
            if (delays.Count == 2) enabled = true; // user enabled setting between checks
            if (delays.Count == 3) service!.Dispose();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
        service = new UpdateService(client, Store, () => enabled, Delay);
        service.UpdateFound += u => found.Add(u.Version);

        await service.RunAsync();

        Assert.Equal([UpdateService.StartupDelay, UpdateService.Interval, UpdateService.Interval], delays);
        Assert.Equal(1, client.Checks); // first check skipped: setting was disabled
        Assert.Equal(["0.2.1"], found);
    }

    [Fact]
    public async Task Downloads_RunOneAtATime_AndSameVersionIsReused()
    {
        var gate = new TaskCompletionSource();
        var client = new FakeClient { Download = _ => gate.Task };
        using var service = new UpdateService(client, Store, () => true);
        var update = new UpdateCandidate("0.2.1", null);

        var silent = service.DownloadAsync(update, null, CancellationToken.None); // silent background download
        var progress = new List<int>();
        var manual = service.DownloadAsync(new UpdateCandidate("0.2.1", null), progress.Add, CancellationToken.None); // “Install”
        Assert.False(manual.IsCompleted); // waits for silent, not parallel download

        gate.SetResult();
        await Task.WhenAll(silent, manual);
        Assert.Equal(1, client.MaxRunning);
        Assert.Equal(["0.2.1"], client.Downloads); // second download same version — already downloaded
        Assert.Equal([100], progress);

        client.Download = null;
        await service.DownloadAsync(new UpdateCandidate("0.2.2", null), null, CancellationToken.None);
        Assert.Equal(["0.2.1", "0.2.2"], client.Downloads);
    }

    [Fact]
    public async Task FailedDownload_IsRetriedAndDoesNotBlockQueue()
    {
        var client = new FakeClient { Download = _ => Task.FromException(new IOException("disk")) };
        using var service = new UpdateService(client, Store, () => true);
        var update = new UpdateCandidate("0.2.1", null);
        await Assert.ThrowsAsync<IOException>(() => service.DownloadAsync(update, null, CancellationToken.None));

        client.Download = null;
        await service.DownloadAsync(update, null, CancellationToken.None);
        Assert.Equal(["0.2.1", "0.2.1"], client.Downloads);
    }

    [Fact]
    public async Task WaitingDownload_CanBeCancelled()
    {
        var gate = new TaskCompletionSource();
        var client = new FakeClient { Download = _ => gate.Task };
        using var service = new UpdateService(client, Store, () => true);
        var first = service.DownloadAsync(new UpdateCandidate("0.2.1", null), null, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        var second = service.DownloadAsync(new UpdateCandidate("0.2.2", null), null, cts.Token);
        cts.Cancel(); // “Cancel” in window while waiting silent download
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        gate.SetResult();
        await first;
        Assert.Equal(["0.2.1"], client.Downloads);
    }

    [Fact]
    public void StateStore_RoundTrip_AndCorruptFileIsEmptyState()
    {
        var store = Store;
        Assert.Null(store.Load().SkippedVersion);
        Assert.True(store.Save(new UpdateState { SkippedVersion = "1.0.0" }));
        Assert.Equal("1.0.0", store.Load().SkippedVersion);
        File.WriteAllText(store.FilePath, "{ not json");
        Assert.Null(store.Load().SkippedVersion);
    }

    [Fact]
    public void StateStore_DevUsesSeparateFile()
        => Assert.NotEqual(UpdateStateStore.Default(dev: false).FilePath, UpdateStateStore.Default(dev: true).FilePath);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }
}

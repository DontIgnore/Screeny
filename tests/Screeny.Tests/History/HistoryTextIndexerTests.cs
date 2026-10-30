using System.Collections.Concurrent;
using Screeny.History;

namespace Screeny.Tests.History;

/// <summary>Фоновое распознавание текста истории с подставным распознавателем; только временная папка.</summary>
public sealed class HistoryTextIndexerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScreenyIndexTests", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png = [1, 2, 3];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private sealed class FakeRecognizer : IHistoryTextRecognizer
    {
        public HistoryTextMarker? Marker { get; set; } = new("fake", HistoryTextIndexer.IndexVersion, ["en-US"]);
        public ConcurrentQueue<string> Calls { get; } = new();
        public Func<string, string> Text { get; set; } = path => "text of " + Path.GetFileName(path);

        public HistoryTextMarker? CurrentMarker() => Marker;

        public string Recognize(string pngPath)
        {
            Calls.Enqueue(Path.GetFileName(pngPath));
            return Text(pngPath);
        }
    }

    private (HistoryTextIndexer Indexer, HistoryStore Store, FakeRecognizer Recognizer) Create(
        Func<bool>? enabled = null, Func<bool>? paused = null, int limit = 50)
    {
        var store = new HistoryStore(_dir, limit);
        var recognizer = new FakeRecognizer();
        var indexer = new HistoryTextIndexer(() => store, enabled ?? (() => true), paused ?? (() => false), recognizer,
            new BackgroundQueue(), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
        return (indexer, store, recognizer);
    }

    private static async Task Idle(HistoryTextIndexer indexer)
    {
        // Новый проход может начаться внутри Start/Add: ждём, пока текущий не станет последним.
        for (int i = 0; i < 100; i++)
        {
            var run = indexer.WhenIdle();
            await run.WaitAsync(Timeout);
            if (ReferenceEquals(run, indexer.WhenIdle())) return;
        }
    }

    private string Save(HistoryStore store, int second) => store.Save(Png, null, new DateTime(2026, 1, 1).AddSeconds(second));

    [Fact]
    public async Task NewSnapshot_IsRecognized_AndRaisesIndexed()
    {
        var (indexer, store, _) = Create();
        var indexed = new ConcurrentBag<(string, string)>();
        indexer.Indexed += (p, t) => indexed.Add((p, t));
        var path = Save(store, 0);

        indexer.Add(path);
        await Idle(indexer);

        var text = HistoryText.TryRead(Assert.Single(store.List()).TextPath)!;
        Assert.Equal("text of 20260101_000000_000.png", text.Text);
        Assert.Equal("fake", text.Marker.Engine);
        Assert.Equal((path, text.Text), Assert.Single(indexed));
    }

    [Fact]
    public async Task Backfill_NewestFirst_SkipsAlreadyIndexed_AndIsResumable()
    {
        var (indexer, store, recognizer) = Create();
        var oldest = Save(store, 0);
        Save(store, 1);
        Save(store, 2);
        store.WriteText(oldest, new HistoryText("done", recognizer.Marker!));

        indexer.Start();
        await Idle(indexer);
        Assert.Equal(["20260101_000002_000.png", "20260101_000001_000.png"], recognizer.Calls);
        Assert.Equal("done", HistoryText.TryRead(HistoryStore.TextPath(oldest))!.Text);

        indexer.Start(); // следующий запуск: всё уже распознано
        await Idle(indexer);
        Assert.Equal(2, recognizer.Calls.Count);
    }

    [Fact]
    public async Task ChangedMarker_ReindexesEverything()
    {
        var (indexer, store, recognizer) = Create();
        var path = Save(store, 0);
        store.WriteText(path, new HistoryText("old", new HistoryTextMarker("fake", HistoryTextIndexer.IndexVersion, ["en-US", "ru"])));
        indexer.Start();
        await Idle(indexer);
        Assert.Single(recognizer.Calls);
        Assert.Equal("text of 20260101_000000_000.png", HistoryText.TryRead(HistoryStore.TextPath(path))!.Text);
    }

    [Fact]
    public async Task EmptyText_IsStored_SoTheSnapshotIsNotRecognizedAgain()
    {
        var (indexer, store, recognizer) = Create();
        recognizer.Text = _ => "";
        Save(store, 0);
        indexer.Start();
        await Idle(indexer);
        indexer.Start();
        await Idle(indexer);
        Assert.Single(recognizer.Calls);
        Assert.Equal("", HistoryText.TryRead(Assert.Single(store.List()).TextPath)!.Text);
    }

    [Fact]
    public async Task Disabled_DoesNothing()
    {
        var (indexer, store, recognizer) = Create(enabled: () => false);
        indexer.Add(Save(store, 0));
        indexer.Start();
        await Idle(indexer);
        Assert.Empty(recognizer.Calls);
        Assert.Null(Assert.Single(store.List()).TextPath);
    }

    [Fact]
    public async Task NoOcrLanguages_DoesNothing()
    {
        var (indexer, store, recognizer) = Create();
        recognizer.Marker = null;
        Save(store, 0);
        indexer.Start();
        await Idle(indexer);
        Assert.Empty(recognizer.Calls);
    }

    [Fact]
    public async Task Paused_WaitsUntilTheOverlayCloses()
    {
        bool paused = true;
        var (indexer, store, recognizer) = Create(paused: () => Volatile.Read(ref paused));
        Save(store, 0);
        indexer.Start();
        await Task.Delay(150);
        Assert.Empty(recognizer.Calls);
        Volatile.Write(ref paused, false);
        await Idle(indexer);
        Assert.Single(recognizer.Calls);
    }

    [Fact]
    public async Task Stop_KeepsRemainingForLater()
    {
        bool paused = true;
        var (indexer, store, recognizer) = Create(paused: () => Volatile.Read(ref paused));
        Save(store, 0);
        indexer.Start();
        indexer.Stop();
        Volatile.Write(ref paused, false);
        await Idle(indexer);
        Assert.Empty(recognizer.Calls);
        indexer.Start();
        await Idle(indexer);
        Assert.Single(recognizer.Calls);
    }

    [Fact]
    public async Task DeletedBeforeRecognition_LeavesNoOrphan()
    {
        var (indexer, store, recognizer) = Create();
        var path = Save(store, 0);
        recognizer.Text = p => { File.Delete(p); return "gone"; };
        indexer.Add(path);
        await Idle(indexer);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task RecognitionFailure_SkipsTheSnapshot_AndGoesOn()
    {
        var (indexer, store, recognizer) = Create();
        var bad = Save(store, 0);
        Save(store, 1);
        recognizer.Text = p => p == bad ? throw new IOException("broken") : "ok";
        indexer.Start();
        await Idle(indexer);
        indexer.Start(); // в этом сеансе битый снимок больше не пробуется
        await Idle(indexer);
        Assert.Equal(2, recognizer.Calls.Count);
        Assert.Null(store.List().Single(i => i.Path == bad).TextPath);
    }

    [Fact]
    public void NeedsIndex_ByMarker()
    {
        var marker = new HistoryTextMarker("fake", 1, ["en-US"]);
        var store = new HistoryStore(_dir, 10);
        var path = Save(store, 0);
        Assert.True(HistoryTextIndexer.NeedsIndex(store.List()[0], marker));
        store.WriteText(path, new HistoryText("x", marker));
        Assert.False(HistoryTextIndexer.NeedsIndex(store.List()[0], marker));
        Assert.True(HistoryTextIndexer.NeedsIndex(store.List()[0], marker with { Version = 2 }));
        File.WriteAllText(HistoryStore.TextPath(path), "{ broken");
        Assert.True(HistoryTextIndexer.NeedsIndex(store.List()[0], marker));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
    }
}

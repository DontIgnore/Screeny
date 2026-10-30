using System.Collections.Concurrent;
using Screeny.History;
using Screeny.Semantic;

namespace Screeny.Tests.Semantic;

/// <summary>Векторы смыслового поиска: формат sidecar, отбор результатов, хранилище и фоновый индексатор с подставной моделью.</summary>
public sealed class SemanticIndexTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScreenySemanticTests", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png = [1, 2, 3];
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static float[] Unit(int dims, int hot, float other = 0)
    {
        var v = new float[dims];
        Array.Fill(v, other);
        v[hot] = 1;
        return SemanticVectors.Normalize(v);
    }

    private static SemanticVectors Vectors(float[]? image, float[]? text, string? sourceText = null)
        => new(SemanticService.CurrentMarker, SemanticVectors.HashText(sourceText ?? (text is null ? null : "t")), image, text);

    [Fact]
    public void Vectors_RoundTrip_AsFp16()
    {
        var v = Vectors(Unit(768, 3, 0.01f), Unit(768, 5, 0.02f), "hello");
        var back = SemanticVectors.TryParse(v.Serialize())!;
        Assert.Equal(v.Marker, back.Marker);
        Assert.Equal(v.TextHash, back.TextHash);
        Assert.Equal(1.0, SemanticVectors.Dot(back.Image!, v.Image!), 3);
        Assert.Equal(1.0, SemanticVectors.Dot(back.Text!, v.Text!), 3);
        Assert.InRange(v.Serialize().Length, 3000, 3200); // ≈3 КБ на снимок
    }

    [Fact]
    public void Vectors_ImageOnly_RoundTrip()
    {
        var back = SemanticVectors.TryParse(Vectors(Unit(8, 1), null).Serialize())!;
        Assert.NotNull(back.Image);
        Assert.Null(back.Text);
        Assert.Equal(0UL, back.TextHash);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(20)]
    public void Vectors_Truncated_AreRejected(int cut)
    {
        var data = Vectors(Unit(16, 1), Unit(16, 2)).Serialize();
        Assert.Null(SemanticVectors.TryParse(data[..(data.Length - 1 - cut)]));
        Assert.Null(SemanticVectors.TryParse([.. data, 0]));
        Assert.Null(SemanticVectors.TryParse("{}"u8.ToArray()));
    }

    [Fact]
    public void HashText_EmptyIsZero_OtherwiseStable()
    {
        Assert.Equal(0UL, SemanticVectors.HashText(null));
        Assert.Equal(0UL, SemanticVectors.HashText("  "));
        Assert.NotEqual(0UL, SemanticVectors.HashText("a"));
        Assert.Equal(SemanticVectors.HashText("счёт"), SemanticVectors.HashText("счёт"));
        Assert.NotEqual(SemanticVectors.HashText("счёт"), SemanticVectors.HashText("счет"));
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(", ' зуз•", false)]
    [InlineData("OK Cancel", false)]
    [InlineData("Счёт на оплату № 17", true)]
    public void UsableText_SkipsRecognitionNoise(string? text, bool usable)
        => Assert.Equal(usable, SemanticVectors.UsableText(text) is not null);

    [Fact]
    public void Score_IsMaxOfImageAndText()
    {
        var q = Unit(4, 0);
        Assert.Equal(1.0, Vectors(Unit(4, 1), Unit(4, 0)).Score(q), 6);
        Assert.Equal(1.0, Vectors(Unit(4, 0), Unit(4, 1)).Score(q), 6);
        Assert.Equal(0.0, Vectors(Unit(4, 1), null).Score(q), 6);
    }

    [Fact]
    public void Rank_KeepsOnlyNearTheBest_BelowLiteralScore_AndSkipsOtherModels()
    {
        var q = SemanticVectors.Normalize([1f, 0f]);
        float[] At(double cos) => [(float)cos, (float)Math.Sqrt(1 - cos * cos)];
        var items = new (string, SemanticVectors?)[]
        {
            ("best", Vectors(At(0.80), null)),
            ("close", Vectors(At(0.77), null)),
            ("far", Vectors(At(0.70), null)),
            ("none", null),
            ("old", new SemanticVectors(new SemanticMarker("other", 1, 70), 0, At(0.99), null)),
            ("perfect", Vectors(null, At(1.0))),
        };
        var hits = SemanticSearchProvider.Rank(q, items);
        Assert.Equal(["perfect"], hits.Select(h => h.Path));
        Assert.True(hits[0].Score < 1.0); // точное совпадение текста (1,0) всегда выше

        hits = SemanticSearchProvider.Rank(q, items.Where(i => i.Item1 != "perfect"));
        Assert.Equal(["best", "close"], hits.Select(h => h.Path));
    }

    [Fact]
    public void Rank_CapsHits()
    {
        var q = Unit(2, 0);
        var items = Enumerable.Range(0, 100).Select(i => ($"p{i}", (SemanticVectors?)Vectors(Unit(2, 0), null)));
        Assert.Equal(SemanticSearchProvider.MaxHits, SemanticSearchProvider.Rank(q, items).Count);
    }

    [Fact]
    public async Task Fusion_LiteralMatchesFirst_ThenSemanticByScore()
    {
        var entries = new[]
        {
            new HistorySearchEntry("a.png", new DateTime(2026, 1, 1), "invoice total"),
            new HistorySearchEntry("b.png", new DateTime(2026, 1, 2), "nothing"),
            new HistorySearchEntry("c.png", new DateTime(2026, 1, 3), null),
        };
        var semantic = new FixedProvider([new("c.png", 0.75), new("a.png", 0.6), new("b.png", 0.7)]);
        var hits = await new HistorySearch(new TextSearchProvider(), semantic).SearchAsync("invoice", entries);
        Assert.Equal(["a.png", "c.png", "b.png"], hits!.Select(h => h.Path));
    }

    private sealed class FixedProvider(IReadOnlyList<HistorySearchHit> hits) : IHistorySearchProvider
    {
        public Task<IReadOnlyList<HistorySearchHit>> SearchAsync(string query, IReadOnlyList<HistorySearchEntry> entries, CancellationToken ct)
            => Task.FromResult(hits);
    }

    [Fact]
    public void Store_VectorSidecar_IsListedDeletedAndNeverAttachedToNewShot()
    {
        var store = new HistoryStore(_dir, 2);
        var a = store.Save(Png, null, new DateTime(2026, 1, 1));
        Assert.True(store.WriteVectors(a, Vectors(Unit(4, 0), null)));
        var item = Assert.Single(store.List());
        Assert.Equal(HistoryStore.VectorPath(a), item.VectorPath);
        Assert.Single(store.ListVectors());

        // Осиротевший .vec со штампом нового снимка к нему не приклеивается.
        var stamp = new DateTime(2026, 1, 2);
        var orphan = Path.Combine(_dir, "20260102_000000_000.vec");
        File.WriteAllBytes(orphan, [1]);
        var b = store.Save(Png, null, stamp);
        Assert.NotEqual(Path.ChangeExtension(orphan, ".png"), b);

        store.Delete(a);
        Assert.False(File.Exists(HistoryStore.VectorPath(a)));
        Assert.False(store.WriteVectors(a, Vectors(Unit(4, 0), null))); // снимка уже нет
        Assert.False(File.Exists(HistoryStore.VectorPath(a)));

        store.Clear();
        Assert.False(File.Exists(orphan));
        File.WriteAllBytes(Path.Combine(_dir, "foo.vec"), [1]); // чужой файл не трогается
        store.Clear();
        Assert.True(File.Exists(Path.Combine(_dir, "foo.vec")));
    }

    [Fact]
    public void Store_DeleteVectors_KeepsSnapshotsAndText()
    {
        var store = new HistoryStore(_dir, 10);
        var a = store.Save(Png, null, new DateTime(2026, 1, 1));
        store.WriteText(a, new HistoryText("hi", new HistoryTextMarker("x", 1, ["en"])));
        store.WriteVectors(a, Vectors(Unit(4, 0), null));
        Assert.Equal(1, store.DeleteVectors());
        var item = Assert.Single(store.List());
        Assert.NotNull(item.TextPath);
        Assert.Null(item.VectorPath);
    }

    private sealed class FakeEncoder : ISemanticItemEncoder
    {
        public SemanticMarker Marker => SemanticService.CurrentMarker;
        public ConcurrentQueue<(string File, string? Text, bool Reused)> Calls { get; } = new();
        public bool Available { get; set; } = true;

        public SemanticVectors? Encode(string pngPath, string? text, float[]? reuseImage)
        {
            if (!Available) return null;
            Calls.Enqueue((Path.GetFileName(pngPath), text, reuseImage is not null));
            return new SemanticVectors(Marker, SemanticVectors.HashText(text), reuseImage ?? Unit(4, 0),
                string.IsNullOrWhiteSpace(text) ? null : Unit(4, 1));
        }
    }

    private (SemanticIndexer Indexer, HistoryStore Store, FakeEncoder Encoder) Create(bool textExpected = true, Func<bool>? paused = null,
        Func<bool>? enabled = null, BackgroundQueue? files = null)
    {
        var store = new HistoryStore(_dir, 50);
        var encoder = new FakeEncoder();
        var indexer = new SemanticIndexer(() => store, enabled ?? (() => true), paused ?? (() => false), () => textExpected, encoder,
            files ?? new BackgroundQueue(), TimeSpan.Zero, TimeSpan.FromMilliseconds(10));
        return (indexer, store, encoder);
    }

    private static async Task Idle(SemanticIndexer indexer)
    {
        for (int i = 0; i < 100; i++)
        {
            var run = indexer.WhenIdle();
            await run.WaitAsync(Timeout);
            if (ReferenceEquals(run, indexer.WhenIdle())) return;
        }
    }

    private static readonly HistoryTextMarker TextMarker = new("fake", 1, ["en"]);

    private string Save(HistoryStore store, int second, string? text)
    {
        var path = store.Save(Png, null, new DateTime(2026, 1, 1).AddSeconds(second));
        if (text is not null) store.WriteText(path, new HistoryText(text, TextMarker));
        return path;
    }

    [Fact]
    public async Task Backfill_NewestFirst_WaitsForText_AndCountsProgress()
    {
        var (indexer, store, encoder) = Create();
        Save(store, 0, "old screenshot text");
        Save(store, 1, null); // текст ещё не распознан: ждёт
        Save(store, 2, ", ' зуз•"); // распознан, но это шум: только картинка
        indexer.Start();
        await Idle(indexer);
        Assert.Equal([("20260101_000002_000.png", (string?)null, false), ("20260101_000000_000.png", "old screenshot text", false)], encoder.Calls);
        Assert.Equal((2, 3), indexer.Progress);
        var items = store.List();
        Assert.Null(items[1].VectorPath);
        Assert.Null(SemanticVectors.TryRead(items[0].VectorPath)!.Text);
        Assert.NotNull(SemanticVectors.TryRead(items[2].VectorPath)!.Text);
    }

    [Fact]
    public async Task FreshShots_AfterBackfill_CountWithoutRescan()
    {
        var (indexer, store, _) = Create();
        Save(store, 0, "old screenshot text");
        var waiting = Save(store, 1, null);
        indexer.Start();
        await Idle(indexer);
        Assert.Equal((1, 2), indexer.Progress);
        store.WriteText(waiting, new HistoryText("text arrived later", TextMarker));
        indexer.Add(waiting);
        await Idle(indexer);
        Assert.Equal((2, 2), indexer.Progress);
        indexer.Add(Save(store, 2, "a new capture"));
        await Idle(indexer);
        Assert.Equal((3, 3), indexer.Progress);
    }

    [Fact]
    public async Task DisabledWhileWaitingToWrite_WritesNothing()
    {
        bool enabled = true;
        var files = new BackgroundQueue();
        var (indexer, store, encoder) = Create(enabled: () => enabled, files: files);
        var path = Save(store, 0, "some text");
        using var gate = new ManualResetEventSlim();
        var blocker = files.Enqueue(() => gate.Wait(Timeout));
        indexer.Add(path);
        for (int i = 0; i < 200 && encoder.Calls.IsEmpty; i++) await Task.Delay(10);
        Assert.Single(encoder.Calls);
        await Task.Delay(100); // векторы посчитаны и ждут очереди файлов
        enabled = false;
        gate.Set();
        await blocker;
        await Idle(indexer);
        Assert.False(File.Exists(HistoryStore.VectorPath(path)));
    }

    [Fact]
    public async Task NoOcr_IndexesImageOnly()
    {
        var (indexer, store, encoder) = Create(textExpected: false);
        Save(store, 0, null);
        indexer.Start();
        await Idle(indexer);
        Assert.Equal(("20260101_000000_000.png", (string?)null, false), Assert.Single(encoder.Calls));
    }

    [Fact]
    public async Task ChangedText_ReembedsTextOnly_UpToDateIsSkipped()
    {
        var (indexer, store, encoder) = Create();
        var path = Save(store, 0, "first version of the text");
        indexer.Start();
        await Idle(indexer);
        indexer.Start();
        await Idle(indexer);
        Assert.Single(encoder.Calls); // второй проход ничего не делает

        store.WriteText(path, new HistoryText("second version of the text", TextMarker));
        indexer.Add(path);
        await Idle(indexer);
        Assert.Equal(("20260101_000000_000.png", (string?)"second version of the text", true), encoder.Calls.Last());
        Assert.Equal(SemanticVectors.HashText("second version of the text"), SemanticVectors.TryRead(HistoryStore.VectorPath(path))!.TextHash);
    }

    [Fact]
    public async Task OtherModelVectors_AreRebuilt()
    {
        var (indexer, store, encoder) = Create();
        var path = Save(store, 0, "some recognized text");
        store.WriteVectors(path, new SemanticVectors(new SemanticMarker("old-model", 1, 280), SemanticVectors.HashText("some recognized text"), Unit(4, 2), null));
        indexer.Start();
        await Idle(indexer);
        Assert.False(Assert.Single(encoder.Calls).Reused);
        Assert.Equal(SemanticService.CurrentMarker, SemanticVectors.TryRead(HistoryStore.VectorPath(path))!.Marker);
    }

    [Fact]
    public async Task Paused_StartsNothing_ThenResumes()
    {
        bool paused = true;
        var (indexer, store, encoder) = Create(paused: () => paused);
        Save(store, 0, "x");
        indexer.Start();
        await Task.Delay(100);
        Assert.Empty(encoder.Calls);
        paused = false;
        await Idle(indexer);
        Assert.Single(encoder.Calls);
    }

    [Fact]
    public async Task ModelUnavailable_StopsThePass()
    {
        var (indexer, store, encoder) = Create();
        encoder.Available = false;
        Save(store, 0, "x");
        Save(store, 1, "y");
        indexer.Start();
        await Idle(indexer);
        Assert.Empty(encoder.Calls);
        Assert.All(store.List(), i => Assert.Null(i.VectorPath));
    }

    [Fact]
    public void TargetSize_KeepsAspect_WithinBudget()
    {
        var (h, w) = EmbeddingGemmaEncoder.TargetSize(1080, 1920, EmbeddingGemmaEncoder.SoftTokens * 9);
        Assert.Equal(0, h % 48);
        Assert.Equal(0, w % 48);
        Assert.True(h / 16 * (w / 16) <= EmbeddingGemmaEncoder.SoftTokens * 9);
        Assert.InRange((double)w / h, 1.6, 1.95);
    }

    [Fact]
    public void Preprocess_PadsPatchesAndPositions()
    {
        var img = new RgbImage(Enumerable.Repeat((byte)255, 100 * 60 * 3).ToArray(), 100, 60);
        var (patches, pos, valid) = EmbeddingGemmaEncoder.Preprocess(img, softTokens: 4);
        Assert.Equal(36 * 768, patches.Length);
        Assert.Equal(36 * 2, pos.Length);
        Assert.Equal(0, valid % 9);
        Assert.All(patches.Take(valid * 768), p => Assert.Equal(1f, p, 3));
        Assert.All(pos.Skip(valid * 2), p => Assert.Equal(-1L, p));
    }
}

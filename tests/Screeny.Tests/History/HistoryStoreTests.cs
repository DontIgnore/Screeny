using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.History;

namespace Screeny.Tests.History;

public class HistoryStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScreenyTests", Guid.NewGuid().ToString("N"));

    private static BitmapSource MakeBitmap(int w = 4, int h = 4)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 255; px[i + 3] = 255; }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
    }

    [Fact]
    public void Save_CreatesPngNamedByTimestamp()
    {
        var store = new HistoryStore(_dir, limit: 50);
        var path = store.Save(MakeBitmap(), new DateTime(2026, 10, 22, 9, 45, 30, 123));
        Assert.Equal("20261022_094530_123.png", Path.GetFileName(path));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Save_WhenNameCollides_AppendsSuffix()
    {
        var store = new HistoryStore(_dir, 50);
        var t = new DateTime(2026, 1, 1, 0, 0, 0);
        store.Save(MakeBitmap(), t);
        var second = store.Save(MakeBitmap(), t);
        Assert.Equal("20260101_000000_000_1.png", Path.GetFileName(second));
    }

    [Fact]
    public void Save_PrunesOldestBeyondLimit()
    {
        var store = new HistoryStore(_dir, limit: 3);
        for (int i = 0; i < 5; i++) store.Save(MakeBitmap(), new DateTime(2026, 1, 1).AddSeconds(i));
        var items = store.List();
        Assert.Equal(3, items.Count);
        Assert.Equal("20260101_000004_000.png", Path.GetFileName(items[0].Path)); // newest first
        Assert.Equal("20260101_000002_000.png", Path.GetFileName(items[2].Path));
    }

    [Fact]
    public void List_IgnoresNonPngFiles_ForeignPngs_AndMissingDir()
    {
        Assert.Empty(new HistoryStore(_dir, 10).List());
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "x");
        foreach (var name in ForeignPngs) File.WriteAllText(Path.Combine(_dir, name), "x");
        File.WriteAllText(Path.Combine(_dir, "20260101_000000_000.txt"), "x");
        Assert.Empty(new HistoryStore(_dir, 10).List());
    }

    /// <summary>Чужие PNG, сортирующиеся по имени и выше цифр штампа, и ниже, и с похожим префиксом.</summary>
    private static readonly string[] ForeignPngs =
        ["vacation.png", "2019-01-01.png", "IMG_0001.png", "20260101_000000_000_copy.png", "20260101_000000_000x.png"];

    private void WriteForeignPngs()
    {
        Directory.CreateDirectory(_dir);
        foreach (var name in ForeignPngs) File.WriteAllText(Path.Combine(_dir, name), "foreign");
    }

    [Fact]
    public void Prune_NeverDeletesForeignPng()
    {
        WriteForeignPngs();
        var store = new HistoryStore(_dir, limit: 2);
        var saved = new List<string>();
        for (int i = 0; i < 3; i++) saved.Add(store.Save(MakeBitmap(), new DateTime(2026, 1, 1).AddSeconds(i)));

        foreach (var name in ForeignPngs) Assert.True(File.Exists(Path.Combine(_dir, name)), name);
        Assert.False(File.Exists(saved[0]));
        Assert.True(File.Exists(saved[1]));
        Assert.True(File.Exists(saved[2]));
        Assert.Equal([saved[2], saved[1]], store.List().Select(i => i.Path));
    }

    [Fact]
    public void Clear_PreservesForeignPng()
    {
        WriteForeignPngs();
        var store = new HistoryStore(_dir, 10);
        store.Save(MakeBitmap(), DateTime.Now);
        store.Clear();
        Assert.Empty(store.List());
        foreach (var name in ForeignPngs) Assert.True(File.Exists(Path.Combine(_dir, name)), name);
    }

    [Fact]
    public void Save_WithOlderTimestamp_KeepsJustSavedFile()
    {
        var store = new HistoryStore(_dir, limit: 2);
        var t = new DateTime(2026, 10, 25, 3, 30, 0);
        store.Save(MakeBitmap(), t);
        var newest = store.Save(MakeBitmap(), t.AddMinutes(30));
        var rolledBack = store.Save(MakeBitmap(), t.AddMinutes(-30)); // часы переведены назад

        Assert.True(File.Exists(rolledBack));
        Assert.True(File.Exists(newest));
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void Save_ManyCollisions_KeepsNewestFirst()
    {
        var store = new HistoryStore(_dir, limit: 50);
        var t = new DateTime(2026, 1, 1, 12, 0, 0, 5);
        var saved = new List<string>();
        for (int i = 0; i < 12; i++) saved.Add(store.Save(MakeBitmap(), t));

        Assert.Equal("20260101_120000_005_11.png", Path.GetFileName(saved[^1]));
        var expected = Enumerable.Reverse(saved).ToList();
        Assert.Equal(expected, store.List().Select(i => i.Path));
    }

    [Fact]
    public void Save_ManyCollisions_PrunesOldestBySuffix()
    {
        var store = new HistoryStore(_dir, limit: 3);
        var t = new DateTime(2026, 1, 1, 12, 0, 0, 5);
        var saved = new List<string>();
        for (int i = 0; i < 12; i++) saved.Add(store.Save(MakeBitmap(), t));
        Assert.Equal([saved[11], saved[10], saved[9]], store.List().Select(i => i.Path));
    }

    [Fact]
    public void List_WhenFolderUnreadable_ReturnsEmpty()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(file, "x");
        var store = new HistoryStore(file, 10);
        Assert.Empty(store.List());
        store.Clear(); // не бросает
    }

    [Fact]
    public void ListOrThrow_WhenFolderUnreadable_Throws_ButMissingFolderIsEmpty()
    {
        Directory.CreateDirectory(_dir);
        var file = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(file, "x");
        Assert.ThrowsAny<IOException>(() => new HistoryStore(file, 10).ListOrThrow()); // окну истории нужна причина
        Assert.Empty(new HistoryStore(Path.Combine(_dir, "missing"), 10).ListOrThrow());
    }

    [Fact]
    public void Delete_RemovesFile()
    {
        var store = new HistoryStore(_dir, 10);
        var p = store.Save(MakeBitmap(), DateTime.Now);
        store.Delete(p);
        Assert.False(File.Exists(p));
    }

    [Theory]
    [InlineData("20261022_094530_123.png", 2026, 10, 22, 9, 45, 30, 123)]
    [InlineData("20260101_000000_000_1.png", 2026, 1, 1, 0, 0, 0, 0)]
    public void TryParseTimestamp_ReadsNameStamp(string name, int y, int mo, int d, int h, int mi, int s, int ms)
    {
        Assert.True(HistoryStore.TryParseTimestamp(name, out var t));
        Assert.Equal(new DateTime(y, mo, d, h, mi, s, ms), t);
    }

    [Theory]
    [InlineData("vacation.png")]
    [InlineData("2019-01-01.png")]
    [InlineData("20260101_000000_000_copy.png")]
    [InlineData("20260101_000000_000x.png")]
    [InlineData("20260101_000000_000.txt")]
    public void TryParseTimestamp_RejectsForeignNames(string name) => Assert.False(HistoryStore.TryParseTimestamp(name, out _));

    [Fact]
    public void List_UsesTimestampFromName()
    {
        var store = new HistoryStore(_dir, 10);
        store.Save(MakeBitmap(), new DateTime(2025, 5, 6, 7, 8, 9, 10));
        Assert.Equal(new DateTime(2025, 5, 6, 7, 8, 9, 10), store.List()[0].CreatedAt);
    }

    [Fact]
    public void Clear_RemovesOnlyOwnSnapshots()
    {
        var store = new HistoryStore(_dir, 10);
        store.Save(MakeBitmap(), DateTime.Now);
        File.WriteAllText(Path.Combine(_dir, "keep.txt"), "x");
        File.WriteAllText(Path.Combine(_dir, "keep.png"), "x");
        store.Clear();
        Assert.Empty(store.List());
        Assert.True(File.Exists(Path.Combine(_dir, "keep.txt")));
        Assert.True(File.Exists(Path.Combine(_dir, "keep.png")));
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}

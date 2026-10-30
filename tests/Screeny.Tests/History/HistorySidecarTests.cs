using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.History;

namespace Screeny.Tests.History;

/// <summary>Sidecar-файлы снимка истории: <c>&lt;stamp&gt;.screeny.json</c> и <c>&lt;stamp&gt;.orig.png</c>.</summary>
public class HistorySidecarTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScreenyTests", Guid.NewGuid().ToString("N"));
    private static readonly byte[] Png = [1, 2, 3];
    private static readonly byte[] OrigPng = [4, 5, 6, 7];
    private static readonly HistorySidecar Both = new("{\"version\":1}", OrigPng);

    private static readonly string[] ForeignSidecars =
        ["foo.screeny.json", "bar.orig.png", "20260101_000000_000_copy.screeny.json", "2026.orig.png", "20260101_000000_000.orig.png.bak",
         "20261301_000000_000.screeny.json", "20260230_000000_000.orig.png", "foo.text.json", "20260101_000000_000.text.json.bak"];

    private static readonly HistoryTextMarker Marker = new("windows-ocr", 1, ["en-US", "ru"]);

    private string P(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Save_WithSidecar_WritesAllFiles_AndListReportsThem()
    {
        var store = new HistoryStore(_dir, 10);
        var path = store.Save(Png, Both, new DateTime(2026, 10, 24, 12, 0, 0, 5));

        Assert.Equal("20261024_120000_005.png", Path.GetFileName(path));
        Assert.Equal(Png, File.ReadAllBytes(path));
        Assert.Equal("{\"version\":1}", File.ReadAllText(P("20261024_120000_005.screeny.json")));
        Assert.Equal(OrigPng, File.ReadAllBytes(P("20261024_120000_005.orig.png")));
        var item = Assert.Single(store.List()); // sidecar-файлы — не отдельные записи
        Assert.Equal(P("20261024_120000_005.screeny.json"), item.JsonPath);
        Assert.Equal(P("20261024_120000_005.orig.png"), item.OriginalPath);
        Assert.DoesNotContain(Directory.GetFiles(_dir), f => f.EndsWith(".tmp"));
    }

    [Fact]
    public void Save_JsonOnly_HasNoOriginal()
    {
        var store = new HistoryStore(_dir, 10);
        store.Save(Png, new HistorySidecar("{}", null));
        var item = Assert.Single(store.List());
        Assert.NotNull(item.JsonPath);
        Assert.Null(item.OriginalPath);
    }

    [Fact]
    public void Save_WithoutSidecar_ItemHasNoSidecarPaths()
    {
        var store = new HistoryStore(_dir, 10);
        store.Save(Png);
        var item = Assert.Single(store.List());
        Assert.Null(item.JsonPath);
        Assert.Null(item.OriginalPath);
    }

    [Fact]
    public void Save_CollisionSuffix_AppliesToSidecars()
    {
        var store = new HistoryStore(_dir, 10);
        var t = new DateTime(2026, 1, 1);
        store.Save(Png, Both, t);
        var second = store.Save(Png, Both, t);
        Assert.Equal("20260101_000000_000_1.png", Path.GetFileName(second));
        Assert.True(File.Exists(P("20260101_000000_000_1.screeny.json")));
        Assert.True(File.Exists(P("20260101_000000_000_1.orig.png")));
    }

    [Fact]
    public void Save_WhenMainRenameFails_RollsBackSidecars()
    {
        var store = new HistoryStore(_dir, 10);
        var t = new DateTime(2026, 2, 2);
        // Папка с именем временного файла снимка: запись в него падает после того, как sidecar-файлы уже записаны.
        Directory.CreateDirectory(P("20260202_000000_000.png.tmp"));
        Assert.ThrowsAny<Exception>(() => store.Save(Png, Both, t));
        Assert.Empty(store.List());
        Assert.False(File.Exists(P("20260202_000000_000.screeny.json")));
        Assert.False(File.Exists(P("20260202_000000_000.orig.png")));
        Assert.False(File.Exists(P("20260202_000000_000.screeny.json.tmp")));
        Assert.False(File.Exists(P("20260202_000000_000.orig.png.tmp")));
    }

    [Fact]
    public void Save_WhenTemporaryFileAlreadyExists_PreservesIt()
    {
        Directory.CreateDirectory(_dir);
        var temp = P("20260202_000000_000.png.tmp");
        File.WriteAllText(temp, "foreign data");

        Assert.ThrowsAny<IOException>(() => new HistoryStore(_dir, 10).Save(Png, Both, new DateTime(2026, 2, 2)));
        Assert.Equal("foreign data", File.ReadAllText(temp));
        Assert.False(File.Exists(P("20260202_000000_000.screeny.json.tmp")));
        Assert.False(File.Exists(P("20260202_000000_000.orig.png.tmp")));
    }

    [Fact]
    public void Save_SkipsNameWhoseOrphanSidecarExists()
    {
        Directory.CreateDirectory(_dir);
        // Сирота без снимка: новый снимок не должен «унаследовать» его оригинал. Prune после сохранения уберёт её.
        File.WriteAllText(P("20260303_000000_000.orig.png"), "stale");
        var path = new HistoryStore(_dir, 10).Save(Png, new HistorySidecar("{}", null), new DateTime(2026, 3, 3));
        Assert.Equal("20260303_000000_000_1.png", Path.GetFileName(path));
        Assert.Null(Assert.Single(new HistoryStore(_dir, 10).List()).OriginalPath);
        Assert.False(File.Exists(P("20260303_000000_000.orig.png")));
    }

    [Fact]
    public void Delete_RemovesSidecars()
    {
        var store = new HistoryStore(_dir, 10);
        var path = store.Save(Png, Both);
        store.Delete(path);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Delete_ForeignPng_KeepsSameNamedForeignSidecars()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(P("foo.png"), "x");
        File.WriteAllText(P("foo.screeny.json"), "x");
        new HistoryStore(_dir, 10).Delete(P("foo.png"));
        Assert.True(File.Exists(P("foo.screeny.json")));
    }

    [Fact]
    public void Prune_RemovesSidecarsOfPrunedSnapshots()
    {
        var store = new HistoryStore(_dir, limit: 2);
        for (int i = 0; i < 3; i++) store.Save(Png, Both, new DateTime(2026, 1, 1).AddSeconds(i));
        Assert.False(File.Exists(P("20260101_000000_000.png")));
        Assert.False(File.Exists(P("20260101_000000_000.screeny.json")));
        Assert.False(File.Exists(P("20260101_000000_000.orig.png")));
        Assert.True(File.Exists(P("20260101_000001_000.orig.png")));
        Assert.True(File.Exists(P("20260101_000002_000.screeny.json")));
        Assert.Equal(6, Directory.GetFiles(_dir).Length);
    }

    [Fact]
    public void Prune_RemovesOrphanSidecars_ButNotForeignOnes()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(P("20250101_000000_000.screeny.json"), "orphan");
        File.WriteAllText(P("20250101_000000_000_3.orig.png"), "orphan");
        foreach (var name in ForeignSidecars) File.WriteAllText(P(name), "foreign");

        new HistoryStore(_dir, 10).Save(Png, Both);

        Assert.False(File.Exists(P("20250101_000000_000.screeny.json")));
        Assert.False(File.Exists(P("20250101_000000_000_3.orig.png")));
        foreach (var name in ForeignSidecars) Assert.True(File.Exists(P(name)), name);
    }

    [Fact]
    public void Clear_RemovesSnapshotsSidecarsAndOrphans_KeepsForeign()
    {
        var store = new HistoryStore(_dir, 10);
        store.Save(Png, Both, new DateTime(2026, 1, 1));
        store.Save(Png, new HistorySidecar("{}", null), new DateTime(2026, 1, 2));
        File.WriteAllText(P("20250101_000000_000.orig.png"), "orphan");
        foreach (var name in ForeignSidecars) File.WriteAllText(P(name), "foreign");
        File.WriteAllText(P("vacation.png"), "foreign");

        store.Clear();

        Assert.Empty(store.List());
        Assert.Equal(ForeignSidecars.Append("vacation.png").Order(), Directory.GetFiles(_dir).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void DeleteOriginals_RemovesOnlyStrictOriginals_KeepsSnapshotsJsonAndForeign()
    {
        var store = new HistoryStore(_dir, 10);
        var first = store.Save(Png, Both, new DateTime(2026, 1, 1));
        store.Save(Png, Both, new DateTime(2026, 1, 1)); // _1
        store.Save(Png, new HistorySidecar("{}", null), new DateTime(2026, 1, 2));
        foreach (var name in ForeignSidecars) File.WriteAllText(P(name), "foreign");
        File.WriteAllText(P("vacation.png"), "foreign");

        Assert.Equal(["20260101_000000_000.orig.png", "20260101_000000_000_1.orig.png"],
            store.ListOriginals().Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(2, store.DeleteOriginals());

        Assert.Empty(store.ListOriginals());
        Assert.Equal(3, store.List().Count);
        Assert.All(store.List(), i => Assert.Null(i.OriginalPath));
        Assert.True(File.Exists(P("20260101_000000_000.screeny.json")));
        Assert.True(File.Exists(first));
        Assert.All(ForeignSidecars.Append("vacation.png"), name => Assert.True(File.Exists(P(name)), name));
    }

    [Fact]
    public void DeleteTexts_RemovesOnlyStrictTexts_KeepsSnapshotsSidecarsAndForeign()
    {
        var store = new HistoryStore(_dir, 10);
        var first = store.Save(Png, Both, new DateTime(2026, 1, 1));
        var second = store.Save(Png, Both, new DateTime(2026, 1, 2));
        File.WriteAllText(HistoryStore.TextPath(first), "{}");
        File.WriteAllText(HistoryStore.TextPath(second), "{}");
        foreach (var name in ForeignSidecars) File.WriteAllText(P(name), "foreign");

        Assert.Equal(["20260101_000000_000.text.json", "20260102_000000_000.text.json"],
            store.ListTexts().Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Equal(2, store.DeleteTexts());

        Assert.Empty(store.ListTexts());
        Assert.Equal(2, store.List().Count);
        Assert.All(store.List(), i => Assert.Null(i.TextPath));
        Assert.Equal(2, store.ListOriginals().Count);
        Assert.All(ForeignSidecars, name => Assert.True(File.Exists(P(name)), name));
    }

    [Fact]
    public void Clear_RemovesRecognizedText()
    {
        var store = new HistoryStore(_dir, 10);
        var shot = store.Save(Png, Both, new DateTime(2026, 1, 1));
        File.WriteAllText(HistoryStore.TextPath(shot), "{}");
        File.WriteAllText(P("20250101_000000_000.text.json"), "orphan");
        store.Clear();
        Assert.Empty(store.ListTexts());
    }

    [Fact]
    public void ListOriginals_MissingFolder_IsEmpty()
    {
        var store = new HistoryStore(P("missing"), 10);
        Assert.Empty(store.ListOriginals());
        Assert.Equal(0, store.DeleteOriginals());
    }

    [Fact]
    public void OverlayOriginal_KeepOriginalsOff_NothingToWrite()
    {
        var px = new byte[4 * 4 * 4];
        var shot = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, px, 16);
        var region = new Screeny.Overlay.PixelRect(1, 1, 2, 2);
        Assert.Null(Screeny.Overlay.OverlayWindow.OriginalForHistory(false, shot, region));
        var original = Screeny.Overlay.OverlayWindow.OriginalForHistory(true, shot, region);
        Assert.NotNull(original);
        Assert.Equal((2, 2), (original!.PixelWidth, original.PixelHeight));

        // Без оригинала запись истории не получает .orig.png.
        var store = new HistoryStore(_dir, 10);
        store.Save(Png, new HistorySidecar("{}", null), new DateTime(2026, 2, 2));
        Assert.Empty(store.ListOriginals());
        Assert.False(File.Exists(P("20260202_000000_000.orig.png")));
    }

    [Fact]
    public void SidecarPaths_StripPngExtension()
    {
        var (json, original) = HistoryStore.SidecarPaths(@"C:\h\20260101_000000_000_2.png");
        Assert.Equal(@"C:\h\20260101_000000_000_2.screeny.json", json);
        Assert.Equal(@"C:\h\20260101_000000_000_2.orig.png", original);
    }

    [Fact]
    public void Save_BitmapOverload_StillWorks()
    {
        var px = new byte[16];
        var bmp = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Bgra32, null, px, 8);
        var path = new HistoryStore(_dir, 10).Save(bmp, new DateTime(2026, 5, 5));
        Assert.Equal("20260505_000000_000.png", Path.GetFileName(path));
    }

    [Fact]
    public void WriteText_ListReportsTextPath_AndItRoundTrips()
    {
        var store = new HistoryStore(_dir, 10);
        var path = store.Save(Png, Both, new DateTime(2026, 4, 4));
        Assert.Null(Assert.Single(store.List()).TextPath); // старый снимок без текста загружается как раньше

        Assert.True(store.WriteText(path, new HistoryText("Invoice № 42\nСчёт", Marker)));

        var item = Assert.Single(store.List()); // текст — не отдельная запись
        Assert.Equal(P("20260404_000000_000.text.json"), item.TextPath);
        var text = HistoryText.TryRead(item.TextPath)!;
        Assert.Equal("Invoice № 42\nСчёт", text.Text);
        Assert.True(text.Marker.SameAs(Marker));
        Assert.Equal(P("20260404_000000_000.screeny.json"), item.JsonPath); // остальные sidecar-файлы на месте
        Assert.DoesNotContain(Directory.GetFiles(_dir), f => f.EndsWith(".tmp"));
    }

    [Fact]
    public void WriteText_Overwrites()
    {
        var store = new HistoryStore(_dir, 10);
        var path = store.Save(Png);
        store.WriteText(path, new HistoryText("old", Marker));
        store.WriteText(path, new HistoryText("new", Marker with { Version = 2 }));
        var text = HistoryText.TryRead(HistoryStore.TextPath(path))!;
        Assert.Equal("new", text.Text);
        Assert.Equal(2, text.Marker.Version);
    }

    [Fact]
    public void WriteText_DeletedOrForeignSnapshot_WritesNothing()
    {
        var store = new HistoryStore(_dir, 10);
        var path = store.Save(Png);
        store.Delete(path);
        Assert.False(store.WriteText(path, new HistoryText("x", Marker)));
        File.WriteAllText(P("vacation.png"), "foreign");
        Assert.False(store.WriteText(P("vacation.png"), new HistoryText("x", Marker)));
        Assert.Equal(["vacation.png"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Delete_RemovesTextSidecar()
    {
        var store = new HistoryStore(_dir, 10);
        var path = store.Save(Png, Both);
        store.WriteText(path, new HistoryText("x", Marker));
        store.Delete(path);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Prune_RemovesTextOfPrunedSnapshots_AndOrphanText()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(P("20250101_000000_000.text.json"), "orphan");
        var store = new HistoryStore(_dir, limit: 1);
        var first = store.Save(Png, null, new DateTime(2026, 1, 1));
        store.WriteText(first, new HistoryText("x", Marker));
        store.Save(Png, null, new DateTime(2026, 1, 2));
        Assert.Equal(["20260102_000000_000.png"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Save_SkipsNameWhoseOrphanTextExists()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(P("20260303_000000_000.text.json"), "stale");
        var path = new HistoryStore(_dir, 10).Save(Png, null, new DateTime(2026, 3, 3));
        Assert.Equal("20260303_000000_000_1.png", Path.GetFileName(path));
        Assert.Null(Assert.Single(new HistoryStore(_dir, 10).List()).TextPath);
    }

    [Fact]
    public void TextPath_StripsPngExtension()
        => Assert.Equal(@"C:\h\20260101_000000_000_2.text.json", HistoryStore.TextPath(@"C:\h\20260101_000000_000_2.png"));

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}

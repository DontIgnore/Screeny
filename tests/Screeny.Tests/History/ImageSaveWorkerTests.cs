using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Export;
using Screeny.History;

namespace Screeny.Tests.History;

public sealed class ImageSaveWorkerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScreenySaveTests", Guid.NewGuid().ToString("N"));

    private static BitmapSource Image(byte value)
    {
        var image = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { value, value, value, 255 }, 4);
        image.Freeze();
        return image;
    }

    [Fact]
    public async Task SavesSnapshotsInOrderWithMatchingSidecars()
    {
        var worker = new ImageSaveWorker();
        var store = new HistoryStore(_dir, 10);
        var timestamp = new DateTime(2026, 10, 27);
        var first = worker.SaveAsync(store, Image(10), "first", null, null, null, timestamp);
        var second = worker.SaveAsync(store, Image(20), "second", null, null, null, timestamp);
        Assert.True((await first).InHistory);
        Assert.True((await second).InHistory);
        var items = store.List();
        Assert.Equal(2, items.Count);
        Assert.Equal("second", File.ReadAllText(items[0].JsonPath!));
        Assert.Equal("first", File.ReadAllText(items[1].JsonPath!));
    }

    [Fact]
    public async Task HistoryFailure_DoesNotPreventExplicitFileSave()
    {
        Directory.CreateDirectory(_dir);
        var invalidFolder = Path.Combine(_dir, "not-a-directory");
        File.WriteAllText(invalidFolder, "existing");
        var destination = Path.Combine(_dir, "output.png");
        var result = await new ImageSaveWorker().SaveAsync(new HistoryStore(invalidFolder, 10), Image(10), null, null, null, destination, DateTime.Now);
        Assert.False(result.InHistory);
        Assert.NotNull(result.HistoryError);
        Assert.Null(result.SaveError);
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task FileSaveFailure_KeepsHistoryAndOriginal()
    {
        Directory.CreateDirectory(_dir);
        var destination = Path.Combine(_dir, "directory.png");
        Directory.CreateDirectory(destination);
        var store = new HistoryStore(Path.Combine(_dir, "history"), 10);
        var result = await new ImageSaveWorker().SaveAsync(store, Image(10), "{}", Image(20), null, destination, DateTime.Now);
        Assert.True(result.InHistory);
        Assert.NotNull(result.SaveError);
        var entry = Assert.Single(store.List());
        Assert.True(File.Exists(entry.JsonPath));
        Assert.True(File.Exists(entry.OriginalPath));
    }

    [Fact]
    public async Task JpegExport_IsWrittenOnWorker()
    {
        var destination = Path.Combine(_dir, "result.jpg");
        Directory.CreateDirectory(_dir);
        var result = await new ImageSaveWorker().SaveAsync(new HistoryStore(Path.Combine(_dir, "history"), 10), Image(10), null, null, null, destination, DateTime.Now);
        Assert.Null(result.SaveError);
        var bytes = File.ReadAllBytes(destination);
        Assert.Equal((byte)0xff, bytes[0]);
        Assert.Equal((byte)0xd8, bytes[1]);
    }

    [Fact]
    public async Task NoHistoryStore_WritesOnlyExplicitFile()
    {
        Directory.CreateDirectory(_dir);
        var destination = Path.Combine(_dir, "output.png");
        var result = await new ImageSaveWorker().SaveAsync(null, Image(10), "json", Image(20), null, destination, DateTime.Now);
        Assert.False(result.InHistory);
        Assert.Null(result.HistoryError);
        Assert.Null(result.SaveError);
        Assert.NotEmpty(result.Png);
        Assert.Equal([destination], Directory.GetFiles(_dir));
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}

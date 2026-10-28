using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.History;

namespace Screeny.Tests.History;

public class HistoryItemViewModelTests : IDisposable
{
    // '#' и '%20' ломали new Uri(path): '#' уходил во фрагмент, файл «не находился».
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScreenyTests", Guid.NewGuid().ToString("N"), "C# shots %20");

    private string SavePng(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i + 1] = 200; px[i + 3] = 255; }
        var bmp = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        return new HistoryStore(_dir, 10).Save(bmp, new DateTime(2026, 1, 2, 3, 4, 5));
    }

    [Fact]
    public void LoadThumbnail_PathWithHash_DecodesDownscaledFrozenImage()
    {
        var path = SavePng(800, 400);
        var t = HistoryItemViewModel.LoadThumbnail(path);
        Assert.NotNull(t.Image);
        Assert.True(t.Image!.IsFrozen); // создаётся в фоне, используется в UI-потоке
        Assert.Equal(HistoryItemViewModel.ThumbnailWidth, t.Image.PixelWidth);
        Assert.Equal((800, 400), (t.Width, t.Height));
    }

    [Fact]
    public void LoadThumbnail_SmallImage_KeepsSize()
    {
        var t = HistoryItemViewModel.LoadThumbnail(SavePng(50, 30));
        Assert.Equal(50, t.Image!.PixelWidth);
        Assert.Equal((50, 30), (t.Width, t.Height));
    }

    [Theory]
    [InlineData(100, 800)]
    [InlineData(1, 800)]
    public void LoadThumbnail_TallImage_FitsThumbnailHeight(int width, int height)
    {
        var t = HistoryItemViewModel.LoadThumbnail(SavePng(width, height));
        Assert.NotNull(t.Image);
        Assert.True(t.Image!.PixelHeight <= 130);
        Assert.True(t.Image.PixelWidth <= 200);
        Assert.Equal((width, height), (t.Width, t.Height));
    }

    [Fact]
    public void LoadThumbnail_MissingOrBrokenFile_ReturnsEmpty()
    {
        Assert.Null(HistoryItemViewModel.LoadThumbnail(Path.Combine(_dir, "missing.png")).Image);
        Directory.CreateDirectory(_dir);
        var broken = Path.Combine(_dir, "20260101_000000_000.png");
        File.WriteAllText(broken, "not a png");
        Assert.Null(HistoryItemViewModel.LoadThumbnail(broken).Image);
    }

    [Fact]
    public void DateText_UsesTheRegionalFormat()
    {
        var when = new DateTime(2026, 10, 27, 21, 5, 3);
        var vm = new HistoryItemViewModel(new HistoryItem(Path.Combine(_dir, "x.png"), when));
        var saved = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
            Assert.Equal("10/27/2026 9:05:03 PM", vm.DateText);
            System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
            Assert.Equal("27.10.2026 21:05:03", vm.DateText);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = saved; }
    }

    [Fact]
    public void LoadFull_PathWithHash_LoadsAndReleasesFile()
    {
        var path = SavePng(30, 20);
        var vm = new HistoryItemViewModel(new HistoryItem(path, DateTime.Now));
        var full = vm.LoadFull();
        Assert.Equal(30, full.PixelWidth);
        File.Delete(path); // файл не удерживается
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void SetThumbnail_RaisesPropertyChanged()
    {
        var vm = new HistoryItemViewModel(new HistoryItem(SavePng(10, 10), DateTime.Now));
        Assert.Null(vm.Thumbnail);
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        vm.SetThumbnail(HistoryItemViewModel.LoadThumbnail(vm.Path));
        Assert.NotNull(vm.Thumbnail);
        Assert.Equal("10×10", vm.SizeText);
        Assert.Contains(nameof(HistoryItemViewModel.Thumbnail), changed);
        Assert.Contains(nameof(HistoryItemViewModel.SizeText), changed);
    }

    public void Dispose()
    {
        var root = Path.GetDirectoryName(_dir)!;
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

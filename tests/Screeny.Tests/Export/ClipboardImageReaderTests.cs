using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Export;

namespace Screeny.Tests.Export;

/// <summary>«Закрепить из буфера обмена»: какой формат буфера выбирается. Настоящий буфер не трогается.</summary>
public sealed class ClipboardImageReaderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScreenyClipboardTests", Guid.NewGuid().ToString("N"));

    private sealed class FakeClipboard : IClipboardSource
    {
        public Dictionary<string, object> Data { get; } = new();
        public List<string>? Files { get; set; }
        public BitmapSource? Bitmap { get; set; }
        public Exception? Throw { get; set; }

        public bool ContainsData(string format) { if (Throw is not null) throw Throw; return Data.ContainsKey(format); }
        public object? GetData(string format) => Data.GetValueOrDefault(format);
        public IReadOnlyList<string>? GetFileDropList() => Files;
        public BitmapSource? GetBitmap() => Bitmap;
    }

    private static BitmapSource Image(int width, int height)
    {
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, new byte[width * height * 4], width * 4);
        image.Freeze();
        return image;
    }

    private static byte[] Png(int width, int height) => ClipboardService.EncodePng(Image(width, height));

    [Fact]
    public void Png_IsPreferredOverBitmap()
    {
        var clipboard = new FakeClipboard { Bitmap = Image(5, 5) };
        clipboard.Data["PNG"] = new MemoryStream(Png(3, 2));
        var result = ClipboardImageReader.Read(clipboard);
        Assert.Equal(ClipboardImageStatus.Image, result.Status);
        Assert.Equal(3, result.Image!.PixelWidth);
        Assert.Equal(2, result.Image.PixelHeight);
        Assert.True(result.Image.IsFrozen);
    }

    [Fact]
    public void SingleImageFile_IsRead()
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "shot.PNG");
        File.WriteAllBytes(path, Png(4, 7));
        var result = ClipboardImageReader.Read(new FakeClipboard { Files = [path] });
        Assert.Equal(ClipboardImageStatus.Image, result.Status);
        Assert.Equal(7, result.Image!.PixelHeight);
        File.Delete(path); // файл не остаётся открытым
    }

    [Fact]
    public void SeveralFilesOrNotAnImage_FallsBackToBitmapOrNothing()
    {
        Directory.CreateDirectory(_dir);
        var a = Path.Combine(_dir, "a.png");
        var b = Path.Combine(_dir, "b.png");
        var text = Path.Combine(_dir, "notes.txt");
        File.WriteAllBytes(a, Png(1, 1));
        File.WriteAllBytes(b, Png(1, 1));
        File.WriteAllText(text, "hello");

        Assert.Equal(ClipboardImageStatus.NoImage, ClipboardImageReader.Read(new FakeClipboard { Files = [a, b] }).Status);
        Assert.Equal(ClipboardImageStatus.NoImage, ClipboardImageReader.Read(new FakeClipboard { Files = [text] }).Status);
        var withBitmap = ClipboardImageReader.Read(new FakeClipboard { Files = [text], Bitmap = Image(2, 2) });
        Assert.Equal(ClipboardImageStatus.Image, withBitmap.Status);
    }

    [Fact]
    public void BrokenPngOrMissingFile_IsNotAnImage()
    {
        var clipboard = new FakeClipboard { Files = [Path.Combine(_dir, "gone.png")] };
        clipboard.Data["PNG"] = new MemoryStream([1, 2, 3]);
        Assert.Equal(ClipboardImageStatus.NoImage, ClipboardImageReader.Read(clipboard).Status);
    }

    [Fact]
    public void EmptyClipboard_IsNoImage()
        => Assert.Equal(ClipboardImageStatus.NoImage, ClipboardImageReader.Read(new FakeClipboard()).Status);

    [Fact]
    public void BusyClipboard_IsReportedAsBusy()
    {
        var clipboard = new FakeClipboard { Throw = new COMException("busy", ClipboardService.ClipboardCantOpen) };
        Assert.Equal(ClipboardImageStatus.Busy, ClipboardImageReader.Read(clipboard).Status);
    }

    [Theory]
    [InlineData("a.png", true)]
    [InlineData("a.JPEG", true)]
    [InlineData("a.bmp", true)]
    [InlineData("a.txt", false)]
    [InlineData("a.png.lnk", false)]
    [InlineData("noext", false)]
    public void IsImageFile_ByExtension(string name, bool expected) => Assert.Equal(expected, ClipboardImageReader.IsImageFile(name));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}

using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Export;

namespace Screeny.Tests.Export;

public class FileSaverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScreenyFileSaverTests_" + Guid.NewGuid().ToString("N"));

    public FileSaverTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private static BitmapSource Image(int w = 8, int h = 6)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = 10; px[i + 1] = 20; px[i + 2] = 30; px[i + 3] = 255; }
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    private static bool IsPng(byte[] b) => b.Length > 8 && b[0] == 0x89 && b[1] == (byte)'P' && b[2] == (byte)'N' && b[3] == (byte)'G';
    private static bool IsJpeg(byte[] b) => b.Length > 3 && b[0] == 0xFF && b[1] == 0xD8;

    [Theory]
    [InlineData(1, @"C:\x\shot.png", ImageFileFormat.Png, @"C:\x\shot.png")]
    [InlineData(1, @"C:\x\shot.bmp", ImageFileFormat.Png, @"C:\x\shot.bmp.png")]
    [InlineData(1, @"C:\x\shot.jpg", ImageFileFormat.Png, @"C:\x\shot.jpg.png")]
    [InlineData(2, @"C:\x\shot.jpg", ImageFileFormat.Jpeg, @"C:\x\shot.jpg")]
    [InlineData(2, @"C:\x\shot.JPEG", ImageFileFormat.Jpeg, @"C:\x\shot.JPEG")]
    [InlineData(2, @"C:\x\shot.png", ImageFileFormat.Jpeg, @"C:\x\shot.png.jpg")]
    [InlineData(2, @"C:\x\shot", ImageFileFormat.Jpeg, @"C:\x\shot.jpg")]
    public void Resolve_FormatComesFromFilterIndex(int filterIndex, string fileName, ImageFileFormat format, string path)
    {
        Assert.Equal((format, path), FileSaver.Resolve(filterIndex, fileName));
    }

    [Fact]
    public void ResolveForSave_ConfirmsExistingAdjustedPath()
    {
        var entered = Path.Combine(_dir, "shot.jpg");
        var target = entered + ".png";
        File.WriteAllText(target, "existing");
        string? asked = null;
        Assert.Null(FileSaver.ResolveForSave(1, entered, path => { asked = path; return false; }));
        Assert.Equal(target, asked);
        Assert.Equal("existing", File.ReadAllText(target));
        Assert.Equal(target, FileSaver.ResolveForSave(1, entered, _ => true));
    }

    [Fact]
    public void ResolveForSave_DoesNotRepeatTheDialogConfirmationOrPromptForNewFile()
    {
        var existing = Path.Combine(_dir, "shot.png");
        File.WriteAllText(existing, "existing");
        Assert.Equal(existing, FileSaver.ResolveForSave(1, existing, _ => throw new Exception("Unexpected confirmation")));
        var newPath = Path.Combine(_dir, "new.jpg");
        Assert.Equal(newPath + ".png", FileSaver.ResolveForSave(1, newPath, _ => throw new Exception("Unexpected confirmation")));
    }

    [Fact]
    public void Save_WritesRequestedFormat_RegardlessOfExtension()
    {
        var path = Path.Combine(_dir, "a.png");
        FileSaver.Save(Image(), path, ImageFileFormat.Jpeg);
        Assert.True(IsJpeg(File.ReadAllBytes(path)));
        FileSaver.Save(Image(), path, ImageFileFormat.Png);
        Assert.True(IsPng(File.ReadAllBytes(path)));
    }

    [Fact]
    public void Save_ReplacesExistingFile_AndLeavesNoTempFiles()
    {
        var path = Path.Combine(_dir, "b.png");
        File.WriteAllText(path, "old");
        FileSaver.Save(Image(), path);
        Assert.True(IsPng(File.ReadAllBytes(path)));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Save_FailedReplace_KeepsExistingFile_AndRemovesTemp()
    {
        var path = Path.Combine(_dir, "c.png");
        File.WriteAllText(path, "old");
        File.SetAttributes(path, FileAttributes.ReadOnly); // замена падает уже после записи временного файла
        try
        {
            Assert.ThrowsAny<Exception>(() => FileSaver.Save(Image(), path));
            Assert.Equal("old", File.ReadAllText(path));
            Assert.Single(Directory.GetFiles(_dir));
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }
}

public class ClipboardServiceTests
{
    [Fact]
    public void EncodePng_ReturnsDecodablePng()
    {
        var px = new byte[3 * 2 * 4]; Array.Fill(px, (byte)200);
        var src = BitmapSource.Create(3, 2, 96, 96, PixelFormats.Bgra32, null, px, 12);
        var png = ClipboardService.EncodePng(src);
        Assert.Equal(0x89, png[0]);
        var decoded = BitmapDecoder.Create(new MemoryStream(png), BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
        Assert.Equal(3, decoded.PixelWidth);
        Assert.Equal(2, decoded.PixelHeight);
    }
}

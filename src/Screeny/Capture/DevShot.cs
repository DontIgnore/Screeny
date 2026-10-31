using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Overlay;

namespace Screeny.Capture;

/// <summary>
/// A file-backed screenshot for the development instance (<c>--dev --overlay-test x.png</c>), allowing the overlay
/// to be tested without capturing the screen. The image starts at the primary monitor's origin (0, 0).
/// </summary>
public static class DevShot
{
    public static Screenshot Load(string path)
    {
        BitmapSource image;
        using (var stream = File.OpenRead(path))
            image = BitmapFrame.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        return new Screenshot(Normalize(image), new PixelRect(0, 0, image.PixelWidth, image.PixelHeight));
    }

    /// <summary>Bgra32 and 96 DPI, matching a real screenshot as expected by effects and the magnifier.</summary>
    public static BitmapSource Normalize(BitmapSource image)
    {
        var bgra = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight, stride = w * 4;
        var px = new byte[stride * h];
        bgra.CopyPixels(px, stride, 0);
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, stride);
        result.Freeze();
        return result;
    }
}

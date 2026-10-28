using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Overlay;

namespace Screeny.Capture;

/// <summary>
/// «Снимок» из файла для тестового экземпляра (<c>--dev --overlay-test x.png</c>): оверлей проверяется
/// без захвата настоящего экрана. Картинка лежит от начала основного монитора (0, 0).
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

    /// <summary>Bgra32 и 96 DPI — как у настоящего снимка (эффекты и лупа рассчитывают именно на это).</summary>
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

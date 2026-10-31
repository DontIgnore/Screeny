using System.Windows.Media.Imaging;
using Screeny.Overlay;

namespace Screeny.Capture;

/// <summary>Screenshot of the virtual screen. Bitmap is in pixels; VirtualRect is its position on screen.</summary>
public sealed record Screenshot(BitmapSource Bitmap, PixelRect VirtualRect)
{
    public PixelRect PixelBounds => new(0, 0, Bitmap.PixelWidth, Bitmap.PixelHeight);
}

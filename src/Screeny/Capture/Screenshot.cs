using System.Windows.Media.Imaging;
using Screeny.Overlay;

namespace Screeny.Capture;

/// <summary>Снимок виртуального экрана. Bitmap в пикселях; VirtualRect — его положение на экране.</summary>
public sealed record Screenshot(BitmapSource Bitmap, PixelRect VirtualRect)
{
    public PixelRect PixelBounds => new(0, 0, Bitmap.PixelWidth, Bitmap.PixelHeight);
}

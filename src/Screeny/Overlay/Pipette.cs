using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Screeny.Overlay;

/// <summary>Color of screenshot pixel (eyedropper and magnifier).</summary>
public static class Pipette
{
    /// <summary>Opaque color of Bgra32 pixel; point outside screenshot clamped to edge.</summary>
    public static Color ColorAt(BitmapSource shot, PixelPoint p)
    {
        int x = Math.Clamp(p.X, 0, shot.PixelWidth - 1), y = Math.Clamp(p.Y, 0, shot.PixelHeight - 1);
        var px = new byte[4];
        shot.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return Color.FromRgb(px[2], px[1], px[0]);
    }
}

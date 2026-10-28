using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Screeny.Overlay;

/// <summary>Цвет пикселя снимка (пипетка и лупа).</summary>
public static class Pipette
{
    /// <summary>Непрозрачный цвет пикселя Bgra32; точка вне снимка прижимается к краю.</summary>
    public static Color ColorAt(BitmapSource shot, PixelPoint p)
    {
        int x = Math.Clamp(p.X, 0, shot.PixelWidth - 1), y = Math.Clamp(p.Y, 0, shot.PixelHeight - 1);
        var px = new byte[4];
        shot.CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return Color.FromRgb(px[2], px[1], px[0]);
    }
}

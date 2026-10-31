using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Annotations;
using Screeny.Overlay;

namespace Screeny.Export;

public static class ImageComposer
{
    /// <summary>
    /// Cuts region from screenshot and draws annotations on top with same code as on screen.
    /// All in 96 DPI, 1 unit = 1 pixel, no overlay DPI scaling. Result frozen.
    /// </summary>
    public static BitmapSource Compose(BitmapSource shot, PixelRect region, AnnotationDocument? doc)
    {
        var inside = region.Clamp(new PixelRect(0, 0, shot.PixelWidth, shot.PixelHeight));
        bool fullyInside = inside == region && !region.IsEmpty;

        // Without annotations nothing to draw: just copy of region pixels, without RenderTargetBitmap.
        if (fullyInside && (doc is null || doc.Items.Count == 0))
            return CopyRegion(shot, region);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, region.Width, region.Height)));
            // Only region pixels, not whole screenshot under clip.
            if (!inside.IsEmpty)
                dc.DrawImage(new CroppedBitmap(shot, inside.ToInt32Rect()),
                    new Rect(inside.X - region.X, inside.Y - region.Y, inside.Width, inside.Height));
            dc.PushTransform(new TranslateTransform(-region.X, -region.Y));
            // Effects compute their region in whole screenshot coordinates — pass it.
            doc?.Render(dc, new RenderContext(shot, ForExport: true));
            dc.Pop();
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(region.Width, region.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>
    /// Independent copy of region pixels. CroppedBitmap alone would hold reference to whole screenshot
    /// of virtual screen (up to ~100 MB) while result lives in pin window or history.
    /// </summary>
    private static BitmapSource CopyRegion(BitmapSource shot, PixelRect region)
    {
        var crop = new CroppedBitmap(shot, region.ToInt32Rect());
        int stride = (region.Width * shot.Format.BitsPerPixel + 7) / 8;
        var px = new byte[stride * region.Height];
        crop.CopyPixels(px, stride, 0);
        var result = BitmapSource.Create(region.Width, region.Height, 96, 96, shot.Format, shot.Palette, px, stride);
        result.Freeze();
        return result;
    }
}

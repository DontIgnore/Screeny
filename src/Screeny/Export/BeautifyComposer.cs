using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;

namespace Screeny.Export;

public static class BeautifyComposer
{
    /// <summary>
    /// Decorates ready screenshot: background gradient on whole canvas, screenshot with margin and rounded corners, soft shadow.
    /// Size — screenshot + 2 × margin, 96 DPI, 1 unit = 1 pixel. Result always opaque (alpha 255
    /// in all pixels): DIB paste into Paint/Word would make transparent corners black. Pure function,
    /// result frozen.
    /// </summary>
    public static BitmapSource Compose(BitmapSource content, BeautifyOptions o)
    {
        int w = content.PixelWidth, h = content.PixelHeight, p = o.PaddingPx;
        int width = w + 2 * p, height = h + 2 * p;
        var full = new Rect(0, 0, width, height);
        var inner = new Rect(p, p, w, h);
        double r = Math.Min(o.Radius, Math.Min(w, h) / 2.0);

        var root = new ContainerVisual();

        var background = new DrawingVisual();
        using (var dc = background.RenderOpen())
        {
            // Solid color under gradient: even edge gradient pixels leave no transparency.
            dc.DrawRectangle(Solid(BeautifyOptions.Colors(o.Preset).From), null, full);
            dc.DrawRectangle(BeautifyOptions.Brush(o.Preset), null, full);
        }
        root.Children.Add(background);

        if (o.Shadow)
        {
            var shadow = new DrawingVisual();
            RenderOptions.SetBitmapScalingMode(shadow, BitmapScalingMode.Linear);
            using (var dc = shadow.RenderOpen())
                dc.DrawImage(ShadowMask(width, height, new Rect(inner.X, inner.Y + o.ShadowOffset, inner.Width, inner.Height), r, o.ShadowBlur), full);
            root.Children.Add(shadow);
        }

        var foreground = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(foreground, BitmapScalingMode.NearestNeighbor);
        using (var dc = foreground.RenderOpen())
        {
            if (r > 0) dc.PushClip(new RectangleGeometry(inner, r, r));
            // Rectangle in pixels, not screenshot DIP: screenshot with other DPI not scaled.
            dc.DrawImage(content, inner);
            if (r > 0) dc.Pop();
        }
        root.Children.Add(foreground);

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(root);
        return Opaque(rtb);
    }

    /// <summary>
    /// Blurred shadow at reduced scale: software BlurEffect on 4K screenshot costs hundreds of milliseconds,
    /// while soft shadow after stretching with linear interpolation looks same. Mask over whole canvas.
    /// </summary>
    private static BitmapSource ShadowMask(int width, int height, Rect rect, double radius, double blur)
    {
        double k = Math.Clamp(Math.Floor(blur / 2), 1, 4); // how many times smaller; blur radius ≥ 2 px of mask
        int mw = Math.Max(1, (int)Math.Ceiling(width / k)), mh = Math.Max(1, (int)Math.Ceiling(height / k));
        var visual = new DrawingVisual
        {
            Effect = new BlurEffect { Radius = blur / k, KernelType = KernelType.Gaussian, RenderingBias = RenderingBias.Quality },
        };
        using (var dc = visual.RenderOpen())
        {
            // Transparent rectangle over whole canvas: blur not clipped by shadow boundary.
            dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, mw, mh));
            dc.DrawRoundedRectangle(Solid(Color.FromArgb((byte)Math.Round(255 * BeautifyOptions.ShadowOpacity), 0, 0, 0)), null,
                new Rect(rect.X / k, rect.Y / k, rect.Width / k, rect.Height / k), radius / k, radius / k);
        }
        var rtb = new RenderTargetBitmap(mw, mh, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Result size for screenshot <paramref name="w"/> × <paramref name="h"/>.</summary>
    public static (int Width, int Height) SizeFor(int w, int h, BeautifyOptions o) => (w + 2 * o.PaddingPx, h + 2 * o.PaddingPx);

    /// <summary>
    /// Bgra32 with alpha 255 in all pixels. Background already opaque, so this is just guarantee: pixel
    /// with alpha &lt; 255 (premultiplied) becomes same color over black.
    /// </summary>
    private static BitmapSource Opaque(BitmapSource pbgra)
    {
        int stride = pbgra.PixelWidth * 4;
        var px = new byte[stride * pbgra.PixelHeight];
        pbgra.CopyPixels(px, stride, 0);
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        var result = BitmapSource.Create(pbgra.PixelWidth, pbgra.PixelHeight, 96, 96, PixelFormats.Bgra32, null, px, stride);
        result.Freeze();
        return result;
    }

    private static SolidColorBrush Solid(Color c) { var b = new SolidColorBrush(c); b.Freeze(); return b; }
}

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Annotations;
using Screeny.Overlay;

namespace Screeny.Export;

public static class ImageComposer
{
    /// <summary>
    /// Вырезает регион из снимка и рисует поверх аннотации тем же кодом, что и на экране.
    /// Всё в 96 DPI, 1 единица = 1 пиксель, без DPI-масштаба оверлея. Результат заморожен.
    /// </summary>
    public static BitmapSource Compose(BitmapSource shot, PixelRect region, AnnotationDocument? doc)
    {
        var inside = region.Clamp(new PixelRect(0, 0, shot.PixelWidth, shot.PixelHeight));
        bool fullyInside = inside == region && !region.IsEmpty;

        // Без аннотаций рисовать нечего: просто копия пикселей региона, без RenderTargetBitmap.
        if (fullyInside && (doc is null || doc.Items.Count == 0))
            return CopyRegion(shot, region);

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, region.Width, region.Height)));
            // Только пиксели региона, а не весь снимок под клипом.
            if (!inside.IsEmpty)
                dc.DrawImage(new CroppedBitmap(shot, inside.ToInt32Rect()),
                    new Rect(inside.X - region.X, inside.Y - region.Y, inside.Width, inside.Height));
            dc.PushTransform(new TranslateTransform(-region.X, -region.Y));
            // Эффекты считают свою область в координатах всего снимка — передаём именно его.
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
    /// Независимая копия пикселей региона. CroppedBitmap сам по себе держал бы ссылку на весь снимок
    /// виртуального экрана (до ~100 МБ), пока результат живёт в pin-окне или истории.
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

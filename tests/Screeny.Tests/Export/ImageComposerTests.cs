using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;
using Screeny.Export;
using Screeny.Overlay;

namespace Screeny.Tests.Export;

public class ImageComposerTests
{
    private static BitmapSource Gray(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = 128; px[i + 3] = 255; }
        return BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
    }

    private static Color PixelAt(BitmapSource bmp, int x, int y)
    {
        var px = new byte[4];
        new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return Color.FromArgb(px[3], px[2], px[1], px[0]);
    }

    [Fact]
    public void Compose_CropsRegion_AndDrawsAnnotationsInSnapshotPixels()
    {
        var doc = new AnnotationDocument();
        doc.Add(new RectAnnotation(new Rect(10, 10, 20, 20), new AnnotationStyle(Colors.Red, 3)));

        var result = ImageComposer.Compose(Gray(50, 50), new PixelRect(5, 5, 30, 30), doc);

        Assert.Equal(30, result.PixelWidth);
        Assert.Equal(30, result.PixelHeight);
        Assert.Equal(Colors.Red, PixelAt(result, 5, 15));                 // левая сторона рамки: x = 10 − 5
        Assert.Equal(Color.FromRgb(128, 128, 128), PixelAt(result, 15, 15)); // внутри рамки — исходный снимок
        Assert.Equal(255, PixelAt(result, 0, 0).A);                          // без прозрачности
    }

    private static BitmapSource Gradient(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        { int i = (y * w + x) * 4; px[i] = (byte)x; px[i + 1] = (byte)y; px[i + 2] = 7; px[i + 3] = 255; }
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compose_EmptyDocument_ReturnsFrozenRegionCopy(bool nullDoc)
    {
        var shot = Gradient(60, 40);
        var result = ImageComposer.Compose(shot, new PixelRect(10, 5, 20, 15), nullDoc ? null : new AnnotationDocument());

        Assert.True(result.IsFrozen);
        Assert.IsNotType<RenderTargetBitmap>(result);
        Assert.Equal(20, result.PixelWidth);
        Assert.Equal(15, result.PixelHeight);
        Assert.Equal(Color.FromArgb(255, 7, 5, 10), PixelAt(result, 0, 0));
        Assert.Equal(Color.FromArgb(255, 7, 19, 29), PixelAt(result, 19, 14));
    }

    [Fact]
    public void Compose_WithAnnotations_KeepsUnannotatedPixelsExact()
    {
        var shot = Gradient(60, 40);
        var doc = new AnnotationDocument();
        doc.Add(new RectAnnotation(new Rect(40, 30, 5, 5), new AnnotationStyle(Colors.Red, 1)));
        var result = ImageComposer.Compose(shot, new PixelRect(10, 5, 20, 15), doc);

        Assert.True(result.IsFrozen);
        Assert.Equal(Color.FromArgb(255, 7, 5, 10), PixelAt(result, 0, 0));
        Assert.Equal(Color.FromArgb(255, 7, 19, 29), PixelAt(result, 19, 14));
    }

    [Fact]
    public void Compose_EffectUsesSnapshotCoordinates()
    {
        // Шахматка: пикселизация внутри региона даёт серый, снаружи эффекта пиксели не тронуты.
        int w = 60, h = 40;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        { int i = (y * w + x) * 4; byte v = (byte)((x + y) % 2 == 0 ? 0 : 255); px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255; }
        var shot = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        var doc = new AnnotationDocument();
        doc.Add(new PixelateAnnotation(new Rect(20, 10, 18, 18), new AnnotationStyle(Colors.Red, 3)));

        var result = ImageComposer.Compose(shot, new PixelRect(10, 5, 40, 30), doc);

        Assert.InRange(PixelAt(result, 12, 7).R, 100, 155);  // (22, 12) снимка — внутри эффекта
        var outside = PixelAt(result, 2, 2).R;              // (12, 7) снимка — вне эффекта
        Assert.True(outside == 0 || outside == 255);
    }
}


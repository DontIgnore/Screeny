using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;

namespace Screeny.Tests.Annotations;

public class RectAnnotationHitTests
{
    [Fact]
    public void SmallShape_CenterHits()
    {
        // Inner area after half thickness and tolerance is empty.
        var a = new RectAnnotation(new Rect(0, 0, 10, 10), new AnnotationStyle(Colors.Red, 3));
        Assert.True(a.HitTest(new Point(5, 5)));
    }

    [Fact]
    public void LargeShape_InteriorStaysTransparent_BorderHits()
    {
        var a = new RectAnnotation(new Rect(0, 0, 100, 100), new AnnotationStyle(Colors.Red, 3));
        Assert.False(a.HitTest(new Point(50, 50)));
        Assert.True(a.HitTest(new Point(100, 50)));
        Assert.False(a.HitTest(new Point(200, 50)));
    }

    [Fact]
    public void ThickBorder_HitsVisibleOuterStrokeButNotEmptyInterior()
    {
        var a = new RectAnnotation(new Rect(20, 20, 100, 100), new AnnotationStyle(Colors.Red, 20));
        Assert.True(a.HitTest(new Point(11, 70)));
        Assert.False(a.HitTest(new Point(45, 70)));
        Assert.False(a.HitTest(new Point(5, 70)));
    }
}

public class PenAnnotationTests
{
    private static AnnotationStyle Style => new(Colors.Red, 4);

    [Fact]
    public void AddPoint_ExtendsBoundsAndHitTest()
    {
        var pen = new PenAnnotation(new Point(10, 10), Style);
        Assert.True(pen.HitTest(new Point(10, 10)));     // single point caught
        Assert.False(pen.HitTest(new Point(100, 10)));
        pen.AddPoint(new Point(100, 10));
        pen.AddPoint(new Point(100, 100));
        Assert.True(pen.HitTest(new Point(55, 11)));
        Assert.True(pen.HitTest(new Point(101, 60)));
        Assert.False(pen.HitTest(new Point(50, 60)));    // inside bounds but off line
        Assert.False(pen.HitTest(new Point(300, 300)));  // outside bounds
        Assert.True(pen.Bounds.Contains(new Point(10, 10)) && pen.Bounds.Contains(new Point(100, 100)));
        Assert.Equal(3, pen.Points.Count);
    }

    [Fact]
    public void Offset_MovesGeometry()
    {
        var pen = new PenAnnotation(new Point(0, 0), Style);
        pen.AddPoint(new Point(50, 0));
        pen.Offset(new Vector(0, 100));
        Assert.True(pen.HitTest(new Point(25, 100)));
        Assert.False(pen.HitTest(new Point(25, 0)));
        Assert.Equal(new Point(50, 100), pen.Points[1]);
    }

    [Fact]
    public void Highlighter_BoundsCoverSquareCaps()
    {
        var hl = new HighlighterAnnotation(new Point(20, 20), Style); // width 16
        hl.AddPoint(new Point(60, 20));
        Assert.True(hl.Bounds.Contains(new Point(12, 12)));
        Assert.True(hl.Bounds.Contains(new Point(68, 28)));
    }
}

public class StyleCacheTests
{
    private static BitmapSource Render(Annotation a, int w = 80, int h = 60)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) a.Render(dc, new RenderContext(Gray(w, h), ForExport: true));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        return rtb;
    }

    private static BitmapSource Gray(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = 128; px[i + 3] = 255; }
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    private static byte[] Pixels(BitmapSource b)
    {
        var px = new byte[b.PixelWidth * b.PixelHeight * 4];
        b.CopyPixels(px, b.PixelWidth * 4, 0);
        return px;
    }

    private static bool Any(byte[] px, Func<byte, byte, byte, byte, bool> pred)
    {
        for (int i = 0; i < px.Length; i += 4) if (pred(px[i], px[i + 1], px[i + 2], px[i + 3])) return true;
        return false;
    }

    [Fact]
    public void StyleChange_RecolorsRect()
    {
        var a = new RectAnnotation(new Rect(10, 10, 40, 30), new AnnotationStyle(Colors.Red, 3));
        Assert.True(Any(Pixels(Render(a)), (b, g, r, al) => r == 255 && g == 0 && b == 0 && al == 255));
        a.Style = new AnnotationStyle(Colors.Blue, 3);
        var px = Pixels(Render(a));
        Assert.True(Any(px, (b, g, r, al) => b == 255 && r == 0 && al == 255));
        Assert.False(Any(px, (b, g, r, al) => r == 255 && b == 0 && al == 255));
    }

    [Fact]
    public void Text_DrawsShadowAndColor_AndUpdatesOnTextChange()
    {
        var t = new TextAnnotation(new Point(2, 2), "Hi", new AnnotationStyle(Colors.Red, 3));
        var w1 = t.Bounds.Width;
        var px = Pixels(Render(t));
        Assert.True(Any(px, (b, g, r, al) => r == 255 && g == 0 && b == 0 && al == 255)); // style color text
        Assert.True(Any(px, (b, g, r, al) => r == 0 && al > 0));                           // dark shadow
        t.Text = "Hello world";
        Assert.True(t.Bounds.Width > w1);
        t.Style = new AnnotationStyle(Colors.Red, 10);
        Assert.True(t.Bounds.Height > 30);
    }

    [Fact]
    public void Arrow_HeadFollowsEndpointChanges()
    {
        var a = new ArrowAnnotation(new Point(5, 30), new Point(40, 30), new AnnotationStyle(Colors.Red, 3));
        Render(a);
        a.End = new Point(70, 30);
        var px = Pixels(Render(a));
        int i = (30 * 80 + 66) * 4; // at tip on new end
        Assert.Equal(255, px[i + 2]);
        Assert.Equal(0, px[i + 1]);
    }

    [Fact]
    public void Number_RendersAndFollowsStyle()
    {
        var n = new NumberAnnotation(new Point(30, 30), 7, new AnnotationStyle(Colors.Red, 3));
        Assert.True(Any(Pixels(Render(n)), (b, g, r, al) => r == 255 && g == 0 && b == 0));
        n.Style = new AnnotationStyle(Colors.Blue, 3);
        Assert.True(Any(Pixels(Render(n)), (b, g, r, al) => b == 255 && g == 0 && r == 0));
    }
}

public class EffectAnnotationCacheTests
{
    private static BitmapSource Noise(int w, int h)
    {
        var px = new byte[w * h * 4];
        new Random(1).NextBytes(px);
        for (int i = 3; i < px.Length; i += 4) px[i] = 255;
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, px, w * 4);
        b.Freeze();
        return b;
    }

    private static IEnumerable<ImageDrawing> Images(Drawing? d)
    {
        if (d is ImageDrawing img) yield return img;
        if (d is DrawingGroup g) foreach (var c in g.Children) foreach (var i in Images(c)) yield return i;
    }

    private static BitmapSource DrawnImage(EffectAnnotation e, BitmapSource src, bool forExport)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) e.Render(dc, new RenderContext(src, forExport));
        var img = Assert.Single(Images(dv.Drawing));
        return (BitmapSource)img.ImageSource;
    }

    [Fact]
    public void Process_IsCached_AndInvalidatedByRectAndStyle()
    {
        var src = Noise(100, 80);
        var e = new BlurAnnotation(new Rect(10, 10, 40, 40), new AnnotationStyle(Colors.Red, 3));
        var first = e.Process(src);
        Assert.Same(first, e.Process(src));
        e.Rect = new Rect(10, 10, 50, 40);
        var second = e.Process(src);
        Assert.NotSame(first, second);
        Assert.Equal(50, second!.PixelWidth);
        e.Style = new AnnotationStyle(Colors.Red, 5);
        Assert.NotSame(second, e.Process(src));
    }

    [Fact]
    public void LiveEdit_RendersDownscaledPreview_ExportStaysFull()
    {
        var src = Noise(200, 120);
        var e = new BlurAnnotation(new Rect(0, 0, 160, 100), new AnnotationStyle(Colors.Red, 3));
        e.BeginLiveEdit();
        Assert.True(e.IsLive);
        var preview = DrawnImage(e, src, forExport: false);
        Assert.Equal(40, preview.PixelWidth);
        Assert.Equal(25, preview.PixelHeight);
        Assert.Equal(160, DrawnImage(e, src, forExport: true).PixelWidth); // export never uses preview

        e.EndLiveEdit();
        Assert.False(e.IsLive);
        Assert.Equal(160, DrawnImage(e, src, forExport: false).PixelWidth);
    }

    [Fact]
    public void LiveEdit_SmallArea_FallsBackToFullQuality()
    {
        var src = Noise(64, 64);
        var e = new PixelateAnnotation(new Rect(3, 3, 50, 50), new AnnotationStyle(Colors.Red, 3));
        e.BeginLiveEdit();
        Assert.Equal(12, DrawnImage(e, src, forExport: false).PixelWidth);
        e.Rect = new Rect(3, 3, 5, 5); // too small for reduction — compute fully
        Assert.Equal(5, DrawnImage(e, src, forExport: false).PixelWidth);
    }
}

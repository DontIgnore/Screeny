using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;

namespace Screeny.Tests.Annotations;

/// <summary>
/// Selection border built from <see cref="Annotation.Bounds"/>: every opaque rendering pixel of shape
/// must be inside Bounds (1 px tolerance for antialiasing).
/// </summary>
public class RenderBoundsTests
{
    private const int W = 420, H = 320;

    private static void Sta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() => { try { body(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    private static readonly BitmapSource Source = CreateSource();

    private static BitmapSource CreateSource()
    {
        var bmp = BitmapSource.Create(W, H, 96, 96, PixelFormats.Bgra32, null, new byte[W * H * 4], W * 4);
        bmp.Freeze();
        return bmp;
    }

    /// <summary>Pixels with nonzero alpha outside Bounds (expanded by tolerance).</summary>
    private static List<(int X, int Y)> Outside(Annotation a, double tolerance = 1)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) a.Render(dc, new RenderContext(Source, ForExport: true));
        var rtb = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        var px = new byte[W * H * 4];
        rtb.CopyPixels(px, W * 4, 0);
        var bounds = a.Bounds;
        bounds.Inflate(tolerance, tolerance);
        var outside = new List<(int, int)>();
        int painted = 0;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            if (px[(y * W + x) * 4 + 3] == 0) continue;
            painted++;
            // Pixel [x, x+1) fully inside border.
            if (x < bounds.Left || x + 1 > bounds.Right || y < bounds.Top || y + 1 > bounds.Bottom) outside.Add((x, y));
        }
        Assert.True(painted > 0, $"{a.GetType().Name}: ничего не нарисовано");
        return outside;
    }

    private static void AssertInside(Annotation a, string what)
    {
        var outside = Outside(a);
        Assert.True(outside.Count == 0,
            $"{what}: {outside.Count} пикселей вне Bounds {a.Bounds}, например {string.Join(" ", outside.Take(6))}");
    }

    private static AnnotationStyle Style(double thickness) => new(Colors.Red, thickness);

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(20)]
    public void Pen_ThickStrokeWithBends_InsideBounds(double thickness) => Sta(() =>
    {
        var pen = new PenAnnotation(new Point(60, 60), Style(thickness));
        // Sharp turns with loops: seams and ends must not exit border.
        foreach (var p in new[] { (200, 70), (90, 120), (260, 200), (120, 250), (300, 90), (310, 240), (180, 160), (330, 260) })
            pen.AddPoint(new Point(p.Item1, p.Item2));
        AssertInside(pen, $"pen {thickness}");
    });

    [Theory]
    [InlineData(20, 1)]
    [InlineData(20, 2)]
    [InlineData(12, 3)]
    public void Pen_DenseJitteryStroke_InsideBounds(double thickness, int seed) => Sta(() =>
    {
        // Like real mouse: points every 1.5–4 px, sharp turns and jitter.
        var rnd = new Random(seed);
        var at = new Point(150, 150);
        var pen = new PenAnnotation(at, Style(thickness));
        double angle = 0;
        for (int i = 0; i < 400; i++)
        {
            angle += (rnd.NextDouble() - 0.5) * (i % 37 == 0 ? 6 : 1.2);
            at += new Vector(Math.Cos(angle), Math.Sin(angle)) * (1.5 + rnd.NextDouble() * 2.5);
            at = new Point(Math.Clamp(at.X, 40, W - 40), Math.Clamp(at.Y, 40, H - 40));
            pen.AddPoint(at);
        }
        AssertInside(pen, $"jittery pen {thickness} seed {seed}");
    });

    [Fact]
    public void Pen_SinglePoint_InsideBounds() => Sta(() =>
    {
        AssertInside(new PenAnnotation(new Point(100, 100), Style(20)), "pen dot");
    });

    [Theory]
    [InlineData(2)]
    [InlineData(10)]
    [InlineData(20)]
    public void Highlighter_InsideBounds(double thickness) => Sta(() =>
    {
        var hl = new HighlighterAnnotation(new Point(80, 80), Style(thickness));
        foreach (var p in new[] { (200, 90), (120, 200), (320, 230) }) hl.AddPoint(new Point(p.Item1, p.Item2));
        AssertInside(hl, $"highlighter {thickness}");
    });

    [Theory]
    [InlineData(2)]
    [InlineData(20)]
    public void Line_InsideBounds(double thickness) => Sta(() =>
    {
        AssertInside(new LineAnnotation(new Point(60, 80), new Point(330, 240), Style(thickness)), $"line {thickness}");
        AssertInside(new LineAnnotation(new Point(60, 150), new Point(330, 150), Style(thickness)), $"horizontal line {thickness}");
    });

    [Theory]
    [InlineData(ArrowStyle.Normal, 3)]
    [InlineData(ArrowStyle.Normal, 20)]
    [InlineData(ArrowStyle.Double, 20)]
    [InlineData(ArrowStyle.Dashed, 12)]
    [InlineData(ArrowStyle.Thick, 4)]
    [InlineData(ArrowStyle.Thick, 20)]
    public void Arrow_InsideBounds(ArrowStyle arrow, double thickness) => Sta(() =>
    {
        var style = Style(thickness) with { Arrow = arrow };
        AssertInside(new ArrowAnnotation(new Point(70, 90), new Point(340, 230), style), $"arrow {arrow} {thickness}");
        AssertInside(new ArrowAnnotation(new Point(340, 60), new Point(80, 70), style), $"arrow back {arrow} {thickness}");
    });

    [Theory]
    [InlineData(1)]
    [InlineData(20)]
    public void RectAndEllipse_StrokeInsideBounds(double thickness) => Sta(() =>
    {
        var box = new Rect(80, 60, 240, 180);
        AssertInside(new RectAnnotation(box, Style(thickness)), $"rect {thickness}");
        AssertInside(new EllipseAnnotation(box, Style(thickness)), $"ellipse {thickness}");
        AssertInside(new RectAnnotation(box, Style(thickness) with { Fill = Colors.Blue }), $"filled rect {thickness}");
    });

    [Fact]
    public void Rect_NoStroke_FillOnly_InsideBounds() => Sta(() =>
    {
        AssertInside(new RectAnnotation(new Rect(80, 60, 240, 180), Style(20) with { StrokeEnabled = false, Fill = Colors.Blue }), "fill only");
    });

    [Theory]
    [InlineData(2, false)]
    [InlineData(8, false)]
    [InlineData(4, true)]
    public void Text_InsideBounds(double thickness, bool background) => Sta(() =>
    {
        var style = Style(thickness) with { TextBackground = background ? Colors.Black : null };
        AssertInside(new TextAnnotation(new Point(40, 60), "Jgy Wq|\nsecond ÁÉ line", style), $"text {thickness} bg={background}");
    });

    [Theory]
    [InlineData(1, 1)]
    [InlineData(20, 12)]
    public void Number_InsideBounds(double thickness, int number) => Sta(() =>
    {
        AssertInside(new NumberAnnotation(new Point(200, 160), number, Style(thickness)), $"number {thickness}");
    });
}

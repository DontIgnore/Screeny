using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;
using Screeny.Export;
using Screeny.Overlay;

namespace Screeny.Tests.Annotations;

public class AnnotationStyleTests
{
    private static readonly Color Red = Color.FromRgb(0xFF, 0x3B, 0x30);
    private static readonly Color Fill40 = Color.FromArgb(0x66, 0xFF, 0x3B, 0x30);

    [Fact]
    public void Equality_IsByValue_IncludingNewFields()
    {
        var a = new AnnotationStyle(Red, 3) { Fill = Fill40, Arrow = ArrowStyle.Dashed };
        var b = new AnnotationStyle(Red, 3) { Fill = Fill40, Arrow = ArrowStyle.Dashed };
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, b with { Fill = null });
        Assert.NotEqual(a, b with { Arrow = ArrowStyle.Thick });
        Assert.NotEqual(a, b with { StrokeEnabled = false });
        Assert.NotEqual(a, b with { TextBackground = Colors.Black });
    }

    [Fact]
    public void Defaults_StrokeOn_NoFill_NormalArrow_NoBackground()
    {
        var s = new AnnotationStyle(Red, 3);
        Assert.True(s.StrokeEnabled);
        Assert.Null(s.Fill);
        Assert.Equal(ArrowStyle.Normal, s.Arrow);
        Assert.Null(s.TextBackground);
        Assert.Equal(Red, s.StrokeColor);
    }

    [Fact]
    public void CannotTurnOffBothStrokeAndFill()
    {
        var strokeOnly = new AnnotationStyle(Red, 3);
        Assert.False(strokeOnly.CanDisableStroke);
        Assert.Same(strokeOnly, strokeOnly.WithStroke(false));    // refusal: style unchanged

        var filled = strokeOnly.WithFill(Fill40);
        var fillOnly = filled.WithStroke(false);
        Assert.False(fillOnly.StrokeEnabled);
        Assert.Null(fillOnly.StrokeColor);
        Assert.False(fillOnly.CanDisableFill);
        Assert.Same(fillOnly, fillOnly.WithFill(null));            // cannot remove last visible part
        Assert.Null(filled.WithFill(null).Fill);                   // with stroke — can
    }

    [Fact]
    public void Json_RoundTrips_EveryArrowStyle_AndNullFill()
    {
        foreach (var arrow in Enum.GetValues<ArrowStyle>())
        {
            var s = new AnnotationStyle(Red, 5) { Arrow = arrow, Fill = null, TextBackground = Color.FromArgb(0xE6, 1, 2, 3) };
            var json = JsonSerializer.Serialize(s);
            Assert.Equal(s, JsonSerializer.Deserialize<AnnotationStyle>(json));
        }
        var filled = new AnnotationStyle(Red, 2.5) { StrokeEnabled = false, Fill = Fill40 };
        Assert.Equal(filled, JsonSerializer.Deserialize<AnnotationStyle>(JsonSerializer.Serialize(filled)));
    }

    [Fact]
    public void Json_UsesHexColorsAndStringEnums()
    {
        var json = JsonSerializer.Serialize(new AnnotationStyle(Red, 3) { Fill = Fill40, Arrow = ArrowStyle.Double });
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        Assert.Equal("#FFFF3B30", root.GetProperty("color").GetString());
        Assert.Equal("#66FF3B30", root.GetProperty("fill").GetString());
        Assert.Equal("Double", root.GetProperty("arrow").GetString());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("textBackground").ValueKind);
        Assert.Equal(3, root.GetProperty("thickness").GetDouble());
        Assert.True(root.GetProperty("stroke").GetBoolean());
    }

    [Fact]
    public void Json_InvisibleStyle_GetsStrokeBack_AndUnknownFieldsAreSkipped()
    {
        var s = JsonSerializer.Deserialize<AnnotationStyle>("""{ "color": "#FF0000FF", "thickness": 4, "stroke": false, "fill": null, "future": [1, 2] }""")!;
        Assert.True(s.StrokeEnabled);
        Assert.Equal(Colors.Blue, s.Color);
    }

    [Theory]
    [InlineData("""{ "color": "nope" }""")]
    [InlineData("""{ "arrow": "Spiral" }""")]
    [InlineData("""{ "thickness": -1 }""")]
    [InlineData("""{ "thickness": 0 }""")]
    [InlineData("""{ "thickness": 1e400 }""")]
    [InlineData("""[1]""")]
    public void Json_Invalid_Throws(string json) => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AnnotationStyle>(json));
}

public class ColorHexTests
{
    [Fact]
    public void Rgb_IsUpperHexWithoutAlpha() => Assert.Equal("#3078F6", ColorHex.Rgb(Color.FromArgb(0x10, 0x30, 0x78, 0xF6)));

    [Fact]
    public void Argb_KeepsAlpha() => Assert.Equal("#6630780A", ColorHex.Argb(Color.FromArgb(0x66, 0x30, 0x78, 0x0A)));

    [Theory]
    [InlineData(40, 0x66)]
    [InlineData(100, 0xFF)]
    [InlineData(0, 0)]
    [InlineData(150, 0xFF)]
    public void WithOpacity_ScalesAlpha(int percent, byte alpha) => Assert.Equal(alpha, ColorHex.WithOpacity(Colors.Red, percent).A);

    [Fact]
    public void OpacityPercent_RoundTripsSteps()
    {
        foreach (var p in StyleOptions.FillOpacitySteps) Assert.Equal(p, ColorHex.OpacityPercent(ColorHex.WithOpacity(Colors.Red, p)));
    }

    [Fact]
    public void TryParse_AcceptsHexAndNames_RejectsGarbage()
    {
        Assert.True(ColorHex.TryParse("#66FF0000", out var c) && c.A == 0x66);
        Assert.True(ColorHex.TryParse("Red", out _));
        Assert.False(ColorHex.TryParse("#GG0000", out _));
        Assert.False(ColorHex.TryParse(null, out _));
    }

    [Fact]
    public void Pipette_ReadsPixel_AsOpaqueColor_AndClampsToEdge()
    {
        var px = new byte[2 * 1 * 4] { 0xF6, 0x78, 0x30, 0xFF, 0x00, 0x00, 0xFF, 0x80 }; // BGRA: #3078F6, semi-transparent red
        var bmp = BitmapSource.Create(2, 1, 96, 96, PixelFormats.Bgra32, null, px, 8);
        Assert.Equal("#3078F6", ColorHex.Rgb(Pipette.ColorAt(bmp, new PixelPoint(0, 0))));
        Assert.Equal(Color.FromRgb(0xFF, 0, 0), Pipette.ColorAt(bmp, new PixelPoint(5, 3)));
    }
}

public class FilledShapeTests
{
    private static AnnotationStyle Filled => new AnnotationStyle(Colors.Red, 3) { Fill = Color.FromArgb(0x66, 255, 0, 0) };

    [Fact]
    public void FilledRect_HitsWholeArea()
    {
        var a = new RectAnnotation(new Rect(0, 0, 100, 100), Filled);
        Assert.True(a.HitTest(new Point(50, 50)));
        Assert.True(a.HitTest(new Point(100, 50)));
        Assert.False(a.HitTest(new Point(200, 50)));
    }

    [Fact]
    public void FillOnlyRect_HitsWholeArea()
    {
        var a = new RectAnnotation(new Rect(0, 0, 100, 100), Filled.WithStroke(false));
        Assert.True(a.HitTest(new Point(50, 50)));
    }

    [Fact]
    public void FilledEllipse_HitsInside_ButNotCorners()
    {
        var filled = new EllipseAnnotation(new Rect(0, 0, 100, 100), Filled);
        Assert.True(filled.HitTest(new Point(50, 50)));
        Assert.False(filled.HitTest(new Point(3, 3)));   // bounds corner outside ellipse
        var outline = new EllipseAnnotation(new Rect(0, 0, 100, 100), new AnnotationStyle(Colors.Red, 3));
        Assert.False(outline.HitTest(new Point(50, 50))); // without fill — outline only
        Assert.True(outline.HitTest(new Point(100, 50)));
    }

    [Fact]
    public void Restyle_ToggleFill_IsOneUndoStep()
    {
        var doc = new AnnotationDocument();
        var a = new RectAnnotation(new Rect(0, 0, 50, 50), new AnnotationStyle(Colors.Red, 3));
        doc.Add(a);
        var before = a.Style;
        doc.Restyle(a, before.WithFill(Color.FromArgb(0x66, 0, 0, 255)).WithStroke(false));
        Assert.False(a.Style.StrokeEnabled);
        Assert.NotNull(a.Style.Fill);
        doc.Undo.Undo();
        Assert.Equal(before, a.Style);
        Assert.Single(doc.Items);                           // only style rolled back
        doc.Undo.Redo();
        Assert.False(a.Style.StrokeEnabled);
    }

    [Fact]
    public void Restyle_SameStyle_AddsNoUndoStep()
    {
        var doc = new AnnotationDocument();
        var a = new RectAnnotation(new Rect(0, 0, 50, 50), Filled);
        doc.Add(a);
        doc.Undo.Undo(); doc.Undo.Redo();
        doc.Restyle(a, Filled); // value-equal but different instance
        doc.Undo.Undo();
        Assert.Empty(doc.Items); // single step — addition
    }

    [Fact]
    public void Export_FillOnlyRect_PaintsInterior_WithoutBorder()
    {
        var px = new byte[40 * 40 * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = 255; px[i + 3] = 255; }
        var shot = BitmapSource.Create(40, 40, 96, 96, PixelFormats.Bgra32, null, px, 160);
        var doc = new AnnotationDocument();
        doc.Add(new RectAnnotation(new Rect(10, 10, 20, 20), new AnnotationStyle(Colors.Red, 3) { StrokeEnabled = false, Fill = Colors.Blue }));
        var result = new FormatConvertedBitmap(ImageComposer.Compose(shot, new PixelRect(0, 0, 40, 40), doc), PixelFormats.Bgra32, null, 0);
        var one = new byte[4];
        result.CopyPixels(new Int32Rect(20, 20, 1, 1), one, 4, 0);
        Assert.Equal(new byte[] { 255, 0, 0, 255 }, one);  // blue inside
        result.CopyPixels(new Int32Rect(10, 20, 1, 1), one, 4, 0);
        Assert.Equal(0, one[2]);                             // on edge also fill, not red border
    }
}

public class ArrowStyleGeometryTests
{
    private static readonly Point A = new(0, 0), B = new(200, 0);

    [Fact]
    public void Normal_HasOneHeadAtEnd_AndLineStopsBeforeTip()
    {
        var s = ArrowGeometry.Compute(A, B, 3, ArrowStyle.Normal);
        var head = Assert.Single(s.Polygons);
        Assert.Equal(B, head[0]);
        Assert.True(s.HasLine);
        Assert.False(s.Dashed);
        Assert.True(s.LineEnd.X < B.X);
        Assert.Equal(A, s.LineStart);
    }

    [Fact]
    public void Double_HasHeadsAtBothEnds()
    {
        var s = ArrowGeometry.Compute(A, B, 3, ArrowStyle.Double);
        Assert.Equal(2, s.Polygons.Count);
        Assert.Contains(s.Polygons, p => p[0] == B);
        Assert.Contains(s.Polygons, p => p[0] == A);
        // Arrowhead wings at start face inside arrow.
        var startHead = s.Polygons.Single(p => p[0] == A);
        Assert.True(startHead[1].X > 0 && startHead[2].X > 0);
        Assert.True(s.LineStart.X > A.X && s.LineEnd.X < B.X);
    }

    [Fact]
    public void Double_ShortArrow_HeadsDoNotOverlap()
    {
        var s = ArrowGeometry.Compute(A, new Point(20, 0), 10, ArrowStyle.Double);
        var end = s.Polygons.Single(p => p[0].X == 20);
        var start = s.Polygons.Single(p => p[0].X == 0);
        Assert.True(start[1].X <= end[1].X); // bases don't intersect
    }

    [Fact]
    public void Dashed_IsDashedLineWithHead()
    {
        var s = ArrowGeometry.Compute(A, B, 3, ArrowStyle.Dashed);
        Assert.True(s.Dashed);
        Assert.Single(s.Polygons);
    }

    [Fact]
    public void Thick_IsOneWedgePolygon_NarrowAtTail_WideAtHead()
    {
        var s = ArrowGeometry.Compute(A, B, 4, ArrowStyle.Thick);
        Assert.False(s.HasLine);
        var poly = Assert.Single(s.Polygons);
        Assert.Equal(7, poly.Length);
        Assert.Equal(B, poly[3]);                                   // tip
        double tail = Math.Abs(poly[0].Y - poly[6].Y), neck = Math.Abs(poly[1].Y - poly[5].Y), wings = Math.Abs(poly[2].Y - poly[4].Y);
        Assert.True(tail < neck && neck < wings);
        Assert.True(ArrowGeometry.HitTest(s, new Point(120, 0), 4));  // body
        Assert.False(ArrowGeometry.HitTest(s, new Point(100, 40), 4));
    }

    [Fact]
    public void ZeroLength_IsEmpty()
    {
        foreach (var style in Enum.GetValues<ArrowStyle>())
            Assert.Empty(ArrowGeometry.Compute(A, A, 3, style).Polygons);
    }

    [Fact]
    public void Annotation_BoundsCoverEveryPoint_ForEveryStyle()
    {
        foreach (var style in Enum.GetValues<ArrowStyle>())
        {
            var a = new ArrowAnnotation(new Point(10, 10), new Point(120, 70), new AnnotationStyle(Colors.Red, 6) { Arrow = style });
            foreach (var poly in a.Shape.Polygons) foreach (var p in poly) Assert.True(a.Bounds.Contains(p), $"{style}: {p}");
            Assert.True(a.HitTest(new Point(65, 40)), style.ToString()); // middle
            Assert.False(a.HitTest(new Point(10, 70)), style.ToString());
        }
    }

    [Fact]
    public void Annotation_RestyleArrow_RebuildsGeometry()
    {
        var doc = new AnnotationDocument();
        var a = new ArrowAnnotation(A, B, new AnnotationStyle(Colors.Red, 3));
        doc.Add(a);
        Assert.Single(a.Shape.Polygons);
        doc.Restyle(a, a.Style with { Arrow = ArrowStyle.Double });
        Assert.Equal(2, a.Shape.Polygons.Count);
        doc.Undo.Undo();
        Assert.Single(a.Shape.Polygons);
    }
}

public class TextBackgroundTests
{
    [Fact]
    public void Background_ExpandsBoundsByPadding_TextStaysInPlace()
    {
        var plain = new TextAnnotation(new Point(100, 100), "Hello", new AnnotationStyle(Colors.White, 3));
        var withBg = new TextAnnotation(new Point(100, 100), "Hello", new AnnotationStyle(Colors.White, 3) { TextBackground = Colors.Black });
        Assert.Equal(plain.Bounds, plain.TextBounds);
        Assert.Equal(plain.TextBounds, withBg.TextBounds);
        var expected = Rect.Inflate(plain.TextBounds, TextAnnotation.BackgroundPadX, TextAnnotation.BackgroundPadY);
        Assert.Equal(expected, withBg.Bounds);
        Assert.True(withBg.Bounds.Contains(withBg.TextBounds));
        Assert.True(withBg.HitTest(new Point(withBg.Bounds.Left + 1, withBg.Bounds.Top + 1)));
    }

    [Fact]
    public void Restyle_Background_UpdatesBounds()
    {
        var doc = new AnnotationDocument();
        var t = new TextAnnotation(new Point(10, 10), "Hi", new AnnotationStyle(Colors.White, 3));
        doc.Add(t);
        var plain = t.Bounds;
        doc.Restyle(t, t.Style with { TextBackground = Colors.Black });
        Assert.True(t.Bounds.Width > plain.Width && t.Bounds.Height > plain.Height);
        doc.Undo.Undo();
        Assert.Equal(plain, t.Bounds);
    }
}

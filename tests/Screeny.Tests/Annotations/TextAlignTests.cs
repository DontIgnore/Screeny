using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;
using Screeny.Overlay;
using Screeny.Overlay.Tools;
using Screeny.Settings;

namespace Screeny.Tests.Annotations;

public class TextAlignTests
{
    private static AnnotationStyle Style(TextAlign align) => new(Colors.White, 3) { TextAlign = align };

    [Theory]
    [InlineData(TextAlign.Left)]
    [InlineData(TextAlign.Center)]
    [InlineData(TextAlign.Right)]
    public void Json_RoundTrips(TextAlign align)
    {
        var s = Style(align) with { TextBackground = Colors.Black };
        var json = JsonSerializer.Serialize(s);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal(align.ToString(), doc.RootElement.GetProperty("textAlign").GetString());
        Assert.Equal(s, JsonSerializer.Deserialize<AnnotationStyle>(json));
    }

    [Fact]
    public void Json_Missing_IsLeft()
        => Assert.Equal(TextAlign.Left, JsonSerializer.Deserialize<AnnotationStyle>("""{ "color": "#FFFFFFFF", "thickness": 3 }""")!.TextAlign);

    [Fact]
    public void Json_Unknown_Throws()
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<AnnotationStyle>("""{ "textAlign": "Justify" }"""));

    [Fact]
    public void Document_RoundTripsAlignedText()
    {
        var text = new TextAnnotation(new Point(5, 6), "one\nlonger line", Style(TextAlign.Right));
        var file = AnnotationSerializer.Deserialize(AnnotationSerializer.Serialize([text], new Point(0, 0), 100, 100, null));
        Assert.Equal(TextAlign.Right, Assert.IsType<TextAnnotation>(Assert.Single(file.Annotations)).Style.TextAlign);
    }

    [Fact]
    public void Default_IsLeft() => Assert.Equal(TextAlign.Left, new AnnotationStyle(Colors.Red, 3).TextAlign);

    [Fact]
    public void BlockSizeAndPosition_DoNotChangeWithAlignment()
    {
        const string text = "Short\nA much longer second line\nmid";
        var left = new TextAnnotation(new Point(40, 30), text, Style(TextAlign.Left));
        foreach (var align in new[] { TextAlign.Center, TextAlign.Right })
        {
            var other = new TextAnnotation(new Point(40, 30), text, Style(align));
            Assert.Equal(left.TextBounds, other.TextBounds);
            Assert.Equal(left.Bounds, other.Bounds);
        }
        // And on style change of same annotation (options strip, undo) block stays in place.
        var before = left.Bounds;
        left.Style = Style(TextAlign.Right);
        Assert.Equal(before, left.Bounds);
    }

    [Fact]
    public void Restyle_ChangesAlignment_AsOneUndoStep()
    {
        var doc = new AnnotationDocument();
        var text = new TextAnnotation(new Point(0, 0), "a\nbb", Style(TextAlign.Left));
        doc.Add(text);
        doc.Restyle(text, Style(TextAlign.Center));
        Assert.Equal(TextAlign.Center, text.Style.TextAlign);
        doc.Undo.Undo();
        Assert.Equal(TextAlign.Left, text.Style.TextAlign);
    }

    [Theory]
    [InlineData(TextAlign.Left)]
    [InlineData(TextAlign.Center)]
    [InlineData(TextAlign.Right)]
    public void ShortLine_IsPlacedInsideBlockByAlignment(TextAlign align)
    {
        var t = new TextAnnotation(new Point(10, 4), "WWWWWWWW\nW", Style(align));
        var block = t.TextBounds;
        var (line1, line2) = InkRanges(t, block);

        // Long line fills block at any alignment: block starts at Origin.
        Assert.InRange(line1.Min, block.Left - 1, block.Left + 3);
        Assert.InRange(line1.Max, block.Right - 4, block.Right + 2);

        double shortWidth = line2.Max - line2.Min;
        switch (align)
        {
            case TextAlign.Left:
                Assert.InRange(line2.Min, block.Left - 1, block.Left + 3);
                break;
            case TextAlign.Right:
                Assert.InRange(line2.Max, block.Right - 4, block.Right + 2);
                break;
            case TextAlign.Center:
                Assert.InRange((line2.Min + line2.Max) / 2.0, block.Left + block.Width / 2 - 3, block.Left + block.Width / 2 + 3);
                break;
        }
        Assert.True(shortWidth < block.Width / 3);
    }

    [Fact]
    public void StyleOptions_TextToolGetsRememberedAlignment_AndAbsorbs()
    {
        var o = StyleOptions.FromSettings(new AppSettings { LastTextAlign = TextAlign.Center }, Colors.Red);
        Assert.Equal(TextAlign.Center, o.StyleFor(ToolKind.Text, Colors.Red, 3).TextAlign);
        Assert.Equal(TextAlign.Left, o.StyleFor(ToolKind.Rect, Colors.Red, 3).TextAlign); // not text — default

        o.Absorb(StyleOptions.WithTextAlign(new AnnotationStyle(Colors.Red, 3), TextAlign.Right), OptionsSectionKind.Text);
        var s = new AppSettings();
        o.SaveTo(s);
        Assert.Equal(TextAlign.Right, s.LastTextAlign);
    }

    /// <summary>Extreme ink columns of first and second text lines (by lower and upper half of block).</summary>
    private static ((int Min, int Max) Line1, (int Min, int Max) Line2) InkRanges(TextAnnotation t, Rect block)
    {
        int w = (int)Math.Ceiling(block.Right) + 20, h = (int)Math.Ceiling(block.Bottom) + 10;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) t.Render(dc, new RenderContext(Blank(), ForExport: true));
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var px = new byte[w * h * 4];
        rtb.CopyPixels(px, w * 4, 0);

        (int, int) Range(int y0, int y1)
        {
            int min = int.MaxValue, max = int.MinValue;
            for (int y = y0; y < y1; y++)
                for (int x = 0; x < w; x++)
                    if (px[(y * w + x) * 4 + 3] > 128) { min = Math.Min(min, x); max = Math.Max(max, x + 1); }
            return (min, max);
        }
        int mid = (int)(block.Top + block.Height / 2);
        return (Range((int)block.Top, mid - 2), Range(mid + 2, (int)block.Bottom));
    }

    private static BitmapSource Blank()
    {
        var b = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
        b.Freeze();
        return b;
    }
}

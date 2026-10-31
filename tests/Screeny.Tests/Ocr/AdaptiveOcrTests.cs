using System.Windows;
using Screeny.Ocr;

namespace Screeny.Tests.Ocr;

public class AdaptiveOcrTests
{
    private static OcrWord W(string t, double x, double y, double w, double h = 12) => new(t, new Rect(x, y, w, h));
    private static OcrLine Line(string text, params OcrWord[] words) => new(words, text);

    [Fact]
    public void Consensus_TakesTheSpellingMostPassesAgreeOn()
    {
        var good = new OcrLayout([Line("срабатывание", W("срабатывание", 0, 0, 90))]);
        var bad = new OcrLayout([Line("срабатыват.е", W("срабатыват.е", 1, 0, 90))]);
        var result = OcrLayout.Consensus([bad, good, good], baseIndex: 0);
        Assert.Equal("срабатывание", result.TextOf(null));
    }

    [Fact]
    public void Consensus_KeepsTheBasePassOnATie()
    {
        var a = new OcrLayout([Line("Hello", W("Hello", 0, 0, 40))]);
        var b = new OcrLayout([Line("He1lo", W("He1lo", 0, 0, 40))]);
        Assert.Equal("He1lo", OcrLayout.Consensus([a, b], baseIndex: 1).TextOf(null));
    }

    [Fact]
    public void Consensus_IgnoresLinesFromOtherPlaces()
    {
        var basis = new OcrLayout([Line("Top", W("Top", 0, 0, 40))]);
        var elsewhere = new OcrLayout([Line("Other", W("Other", 0, 200, 40))]);
        Assert.Equal("Top", OcrLayout.Consensus([basis, elsewhere, elsewhere], baseIndex: 0).TextOf(null));
    }

    [Fact]
    public void Consensus_SpacingDifferencesCountAsTheSameVote()
    {
        var basis = new OcrLayout([Line("a b", W("a", 0, 0, 10), W("b", 14, 0, 10))]);
        var joined = new OcrLayout([Line("ab", W("ab", 0, 0, 24))]);
        var other = new OcrLayout([Line("ax", W("ax", 0, 0, 24))]);
        Assert.Equal("a b", OcrLayout.Consensus([basis, joined, other, other], baseIndex: 0).TextOf(null));
    }

    [Theory]
    [InlineData(9.0, 2560, true)]
    [InlineData(17.9, 400, true)]
    [InlineData(18.0, 400, false)]
    [InlineData(32.0, 300, false)]
    public void NeedsUpscale_DependsOnWordHeight(double height, int longest, bool expected)
        => Assert.Equal(expected, OcrService.NeedsUpscale(height, longest));

    [Theory]
    [InlineData(1600, true)]
    [InlineData(1601, false)]
    public void NeedsUpscale_WhenNothingFound_OnlyForModestImages(int longest, bool expected)
        => Assert.Equal(expected, OcrService.NeedsUpscale(null, longest));

    [Fact]
    public void MedianWordHeight_IsNullForAnEmptyLayout()
        => Assert.Null(OcrService.MedianWordHeight(new OcrLayout([])));

    [Fact]
    public void MedianWordHeight_IgnoresOutliers()
    {
        var layout = new OcrLayout([Line("a b c", W("a", 0, 0, 5, 10), W("b", 10, 0, 5, 11), W("c", 20, 0, 5, 80))]);
        Assert.Equal(11, OcrService.MedianWordHeight(layout));
    }

    [Fact]
    public void ResizeBgra_KeepsFlatColorsExact()
    {
        var src = new byte[3 * 2 * 4];
        for (int i = 0; i < src.Length; i += 4) { src[i] = 10; src[i + 1] = 20; src[i + 2] = 30; src[i + 3] = 255; }
        var dst = ImageResampler.ResizeBgra(src, 3, 2, 7, 5);
        Assert.Equal(7 * 5 * 4, dst.Length);
        for (int i = 0; i < dst.Length; i += 4)
            Assert.Equal(new byte[] { 10, 20, 30, 255 }, dst[i..(i + 4)]);
    }

    [Fact]
    public void ResizeBgra_KeepsAHardEdgeSharpAndInRange()
    {
        // Black left half, white right: after x2 edges stay clean, middle transition.
        int w = 8, h = 1;
        var src = new byte[w * h * 4];
        for (int x = 0; x < w; x++) { byte v = x < 4 ? (byte)0 : (byte)255; src[x * 4] = src[x * 4 + 1] = src[x * 4 + 2] = v; src[x * 4 + 3] = 255; }
        var dst = ImageResampler.ResizeBgra(src, w, h, 16, 2);
        Assert.Equal(0, dst[0]);
        Assert.Equal(255, dst[15 * 4]);
        Assert.Equal(0, dst[5 * 4]);
        Assert.Equal(255, dst[10 * 4]);
    }

    [Fact]
    public void ResizeBgra_SameSizeReturnsACopy()
    {
        var src = new byte[] { 1, 2, 3, 4 };
        var dst = ImageResampler.ResizeBgra(src, 1, 1, 1, 1);
        Assert.Equal(src, dst);
        Assert.NotSame(src, dst);
    }
}

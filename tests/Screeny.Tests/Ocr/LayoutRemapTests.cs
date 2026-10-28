using System.Windows;
using Screeny.Ocr;

namespace Screeny.Tests.Ocr;

public class LayoutRemapTests
{
    private static OcrLayout Layout(params (string Text, double X, double Y, double W)[][] lines)
        => new(lines.Select(l => new OcrLine(l.Select(w => new OcrWord(w.Text, new Rect(w.X, w.Y, w.W, 20))).ToList(),
            string.Join(" ", l.Select(w => w.Text)))).ToList());

    // Windows OCR: «Moonlit» одним словом; Paddle: те же места, но слово разбито иначе и строк больше.
    private static readonly OcrLayout Windows = Layout(
        [("Привет", 0, 0, 60), ("мир", 70, 0, 30)],
        [("Moonlit", 0, 40, 70)]);

    private static readonly OcrLayout Paddle = Layout(
        [("Привет,", 0, 0, 62), ("мир", 72, 0, 30)],
        [("Moon", 0, 40, 35), ("lit", 36, 40, 34)],
        [("SKYLINE", 0, 80, 80)]);

    [Fact]
    public void NoSelection_StaysEmpty() => Assert.Empty(LayoutRemap.Selection(Windows, new HashSet<int>(), Paddle));

    [Fact]
    public void SelectedWord_MapsToWordsAtSamePlace()
        => Assert.Equal([2, 3], LayoutRemap.Selection(Windows, new HashSet<int> { 2 }, Paddle).Order());

    [Fact]
    public void SelectedLine_MapsToSameLineOnly()
        => Assert.Equal([0, 1], LayoutRemap.Selection(Windows, new HashSet<int> { 0, 1 }, Paddle).Order());

    [Fact]
    public void NewWordsOutsideOldSelection_AreNotSelected()
        => Assert.DoesNotContain(4, LayoutRemap.Selection(Windows, Windows.All(), Paddle)); // SKYLINE Windows не нашёл

    [Fact]
    public void TouchingEdges_DoNotCount()
    {
        var from = Layout([("a", 0, 0, 10)]);
        var to = Layout([("b", 10, 0, 10)]); // правый край старого = левый край нового
        Assert.Empty(LayoutRemap.Selection(from, new HashSet<int> { 0 }, to));
    }

    [Fact]
    public void OutOfRangeIndices_AreIgnored()
        => Assert.Equal([0], LayoutRemap.Selection(Windows, new HashSet<int> { 0, 99, -1 }, Paddle).Order());

    [Fact]
    public void Anchor_MapsToFirstWordAtItsPlace()
    {
        Assert.Equal(2, LayoutRemap.Word(Windows, 2, Paddle));
        Assert.Null(LayoutRemap.Word(Windows, null, Paddle));
        Assert.Null(LayoutRemap.Word(Windows, 7, Paddle));
    }
}

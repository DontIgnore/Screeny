using System.Windows;
using Screeny.Ocr;

namespace Screeny.Tests.Ocr;

public class OcrLayoutTests
{
    // Two image “corners”: block left-top and right-bottom, two lines each.
    //   Hello world        (0,0)
    //   second line        (0,20)
    //                         Right corner   (300,200)
    //                         bottom text    (300,220)
    private static OcrLayout Sample() => new(
    [
        Line("Hello world", W("Hello", 0, 0, 40), W("world", 46, 0, 40)),
        Line("second line", W("second", 0, 20, 50), W("line", 56, 20, 30)),
        Line("Right corner", W("Right", 300, 200, 40), W("corner", 346, 200, 50)),
        Line("bottom text", W("bottom", 300, 220, 50), W("text", 356, 220, 30)),
    ]);

    private static OcrWord W(string t, double x, double y, double w) => new(t, new Rect(x, y, w, 12));
    private static OcrLine Line(string text, params OcrWord[] words) => new(words, text);

    [Fact]
    public void Map_UndoesDownscaleAndAddsRegionOffset()
    {
        var layout = new OcrLayout([Line("a", W("a", 10, 20, 30))]).Map(0.5, new Vector(100, 200));
        Assert.Equal(new Rect(120, 240, 60, 24), layout.Word(0).Box);
    }

    [Fact]
    public void InRect_SelectsOnlyTheCornerInsideTheMarquee()
    {
        var layout = Sample();
        var picked = layout.InRect(new Rect(-5, -5, 120, 50));
        Assert.Equal("Hello world" + Environment.NewLine + "second line", layout.TextOf(picked));
    }

    [Fact]
    public void Range_FollowsReadingOrderAcrossLines()
    {
        var layout = Sample();
        Assert.Equal("world" + Environment.NewLine + "second", layout.TextOf(layout.Range(1, 2)));
        Assert.Equal(layout.Range(1, 2), layout.Range(2, 1)); // gesture direction irrelevant
    }

    [Fact]
    public void TextOf_EmptySelectionMeansAllText()
    {
        var layout = Sample();
        Assert.Equal(4, layout.TextOf(new HashSet<int>()).Split(Environment.NewLine).Length);
    }

    [Fact]
    public void TextOf_FullLineUsesEngineText_PartialLineJoinsWords()
    {
        // Engine writes line its way (e.g., punctuation without space) — whole line taken as-is.
        var layout = new OcrLayout([Line("Total: 42", W("Total", 0, 0, 30), W(":", 30, 0, 4), W("42", 38, 0, 12))]);
        Assert.Equal("Total: 42", layout.TextOf(layout.All()));
        Assert.Equal("Total :", layout.TextOf(new HashSet<int> { 0, 1 }));
    }

    [Fact]
    public void TextOf_NoSpaceScriptsJoinPartialSelectionWithoutSpaces()
    {
        var layout = new OcrLayout([Line("日本語です", W("日", 0, 0, 10), W("本", 10, 0, 10), W("語", 20, 0, 10), W("です", 30, 0, 20))], noSpaces: true);
        Assert.Equal("日本語", layout.TextOf(new HashSet<int> { 0, 1, 2 }));
    }

    [Fact]
    public void WordAt_UsesTolerance_AndPicksTheClosestWord()
    {
        var layout = Sample();
        Assert.Equal(0, layout.WordAt(new Point(20, 6)));
        Assert.Equal(1, layout.WordAt(new Point(44, 6), tolerance: 3)); // closer to “world”
        Assert.Null(layout.WordAt(new Point(150, 100)));
    }

    [Fact]
    public void LineRange_SelectsTheWholeLine()
    {
        var layout = Sample();
        Assert.Equal("bottom text", layout.TextOf(layout.LineRange(7)));
    }

    [Fact]
    public void SelectionRuns_MergeNeighboursOnOneLine_AndSplitAcrossLines()
    {
        var layout = Sample();
        var runs = layout.SelectionRuns(new HashSet<int> { 0, 1, 2 }).ToList();
        Assert.Equal(2, runs.Count);
        Assert.Equal(new Rect(0, 0, 86, 12), runs[0]);
    }

    [Fact]
    public void EmptyLinesAreDropped()
    {
        var layout = new OcrLayout([new OcrLine([], ""), Line("x", W("x", 0, 0, 5))]);
        Assert.Equal(1, layout.WordCount);
        Assert.False(layout.IsEmpty);
        Assert.True(OcrLayout.Empty.IsEmpty);
    }

    [Fact]
    public void MergeBest_TakesTheMorePlausibleLineFromAnotherEngine()
    {
        // Russian model reads English line in Cyrillic, English reads Russian in Latin.
        var ru = new OcrLayout([Line("Thank уои", W("Thank", 0, 0, 40), W("уои", 46, 0, 30)), Line("Снимок экрана", W("Снимок", 0, 40, 50), W("экрана", 56, 40, 50))]);
        var en = new OcrLayout([Line("Thank you", W("Thank", 0, 1, 40), W("you", 46, 1, 30)), Line("CHMMOK 3KpaHa", W("CHMMOK", 0, 40, 50), W("3KpaHa", 56, 40, 50))]);
        var merged = OcrLayout.MergeBest([ru, en], 0, OcrService.LinePlausibility);
        Assert.Equal("Thank you" + Environment.NewLine + "Снимок экрана", merged.TextOf(null));
    }

    [Fact]
    public void MergeBest_OneWideLineDoesNotReplaceSeveralBaseLines()
    {
        // Table “Name Value”: one engine splits into two lines, other returns one wide.
        var a = new OcrLayout([Line("Name", W("Name", 0, 0, 40)), Line("Value", W("Value", 300, 0, 40))], language: "en-US");
        var b = new OcrLayout([Line("Name Value", W("Name", 0, 0, 40), W("Value", 300, 0, 40))], language: "ru");
        var merged = OcrLayout.MergeBest([a, b], 0, OcrService.LinePlausibility, OcrService.LanguageFits);
        Assert.Equal("Name" + Environment.NewLine + "Value", merged.TextOf(null));
    }

    [Fact]
    public void MergeBest_KeepsEachLinesOwnSpacingRule()
    {
        // Base Japanese, English line taken from English engine: its part joined with space.
        var ja = new OcrLayout([Line("Hel1o wor1d", W("Hel1o", 0, 0, 40), W("wor1d", 46, 0, 40))], noSpaces: true, language: "ja");
        var en = new OcrLayout([Line("Hello world", W("Hello", 0, 0, 40), W("world", 46, 0, 40))], language: "en-US");
        var merged = OcrLayout.MergeBest([ja, en], 0, OcrService.LinePlausibility, OcrService.LanguageFits);
        Assert.Equal("Hello world", merged.TextOf(new HashSet<int> { 0, 1 }));
        Assert.Equal("Hello", merged.TextOf(new HashSet<int> { 0 }));
    }

    [Theory]
    [InlineData("Invoice", "sr-Latn", true)]
    [InlineData("Счёт", "sr-Cyrl", true)]
    public void LanguageFits_UsesTheScriptSubtag(string line, string tag, bool expected)
        => Assert.Equal(expected, OcrService.LanguageFits(line, tag));

    [Fact]
    public void MergeBest_OnATie_PrefersTheEngineWhoseLanguageFitsTheScript()
    {
        var ru = new OcrLayout([Line("lnvoice 4821", W("lnvoice", 0, 0, 50), W("4821", 56, 0, 30))], language: "ru");
        var en = new OcrLayout([Line("Invoice 4821", W("Invoice", 0, 0, 50), W("4821", 56, 0, 30))], language: "en-US");
        var merged = OcrLayout.MergeBest([ru, en], 0, OcrService.LinePlausibility, OcrService.LanguageFits);
        Assert.Equal("Invoice 4821", merged.TextOf(null));
    }

    [Theory]
    [InlineData("Invoice", "en-US", true)]
    [InlineData("Invoice", "ru", false)]
    [InlineData("Счёт", "ru-RU", true)]
    [InlineData("Счёт", "en-US", false)]
    [InlineData("4821", "en-US", false)]
    public void LanguageFits_MatchesScriptToLanguage(string line, string tag, bool expected)
        => Assert.Equal(expected, OcrService.LanguageFits(line, tag));

    [Fact]
    public void LinePlausibility_KeepsRussianLinesWithLatinTermsRussian()
    {
        Assert.True(OcrService.LinePlausibility("используйте git commit") > OcrService.LinePlausibility("ucnonb3yuTe git commit"));
        Assert.True(OcrService.LinePlausibility("Thank you") > OcrService.LinePlausibility("Thank уои"));
    }

    [Theory]
    [InlineData("ja", true)]
    [InlineData("zh-Hans-CN", true)]
    [InlineData("ru", false)]
    [InlineData("en-US", false)]
    public void IsNoSpaceLanguage(string tag, bool expected) => Assert.Equal(expected, OcrService.IsNoSpaceLanguage(tag));
}

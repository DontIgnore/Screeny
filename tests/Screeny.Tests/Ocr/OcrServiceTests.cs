using Screeny.Ocr;

namespace Screeny.Tests.Ocr;

public class OcrServiceTests
{
    [Theory]
    [InlineData(800, 600, 2600, 1.0)]
    [InlineData(2600, 100, 2600, 1.0)]
    [InlineData(5200, 1000, 2600, 0.5)]
    [InlineData(1000, 3900, 2600, 2.0 / 3)]
    public void ScaleToFit_LimitsLongestSide(int w, int h, int max, double expected)
        => Assert.Equal(expected, OcrService.ScaleToFit(w, h, max), 6);

    [Fact]
    public void Plausibility_PrefersRealWordsOverLatinGarbageForCyrillicText()
    {
        // Так английская модель читает «Esc двойной закрывает pin»
        var garbage = OcrService.Plausibility("Esc ABOäH0i 3aKpb1Ba'0T pin");
        var proper = OcrService.Plausibility("Esc двойной закрывает pin");
        Assert.True(proper > garbage);
    }

    [Fact]
    public void Plausibility_PenalizesMixedScriptsInsideWord()
    {
        Assert.True(OcrService.Plausibility("Committed Task 12") > OcrService.Plausibility("Соmmittеd Таsk 12")); // кириллица внутри латиницы
    }

    [Fact]
    public void PickBest_ReturnsFirstOnTie()
    {
        var best = OcrService.PickBest(["same text", "same text"]);
        Assert.Equal(0, best);
    }

    [Fact]
    public void JoinLines_SeparatesWordsBySpace_AndLinesByNewLine()
    {
        var text = OcrService.JoinLines([["Hello", "world"], ["второй"]]);
        Assert.Equal("Hello world" + Environment.NewLine + "второй", text);
    }
}

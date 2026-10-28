using Screeny.Ocr.Paddle;

namespace Screeny.Tests.Ocr;

public class HomoglyphsTests
{
    [Theory]
    [InlineData("Privaсу", "Privacy")]                    // латинское слово с кириллическими «с», «у»
    [InlineData("PrintSсгееn", "PrintScreen")]
    [InlineData("Соmmittеd", "Committed")]
    [InlineData("Привeт", "Привет")]                      // русское слово с латинской «e»
    [InlineData("Нажмите Privaсу сейчас", "Нажмите Privacy сейчас")]
    public void Fix_ReplacesMinorityScript(string input, string expected) => Assert.Equal(expected, Homoglyphs.Fix(input));

    [Theory]
    [InlineData("Привет")]
    [InlineData("Hello world")]
    [InlineData("X5K7M")]
    [InlineData("ТихийВечер")]
    [InlineData("MrPixel")]
    [InlineData("Wi-Fiсеть")]          // у «т»/«ь» нет латинских двойников — это не гомоглифы, слово как есть
    [InlineData("")]
    public void Fix_LeavesCleanAndGenuinelyMixedWords(string input) => Assert.Equal(input, Homoglyphs.Fix(input));

    [Fact]
    public void Fix_KeepsLength_SoCtcBoundsStayAligned()
    {
        const string input = "Privaсу и PrintSсгееn";
        Assert.Equal(input.Length, Homoglyphs.Fix(input).Length);
    }

    [Fact]
    public void Fix_LatinBAndHBecomeCyrillicCapitals_NotLowercase()
        // Латинские «B» и «H» в кириллическом слове — заглавные «В» и «Н», а не строчные «в»/«н» (их двойники только в части шрифтов).
        => Assert.Equal("ДомНВ", Homoglyphs.Fix("ДомHB"));

    [Theory]
    [InlineData("Privaсу", true)]
    [InlineData("Привет мир", false)]
    [InlineData("Hello мир", false)]
    public void HasMixedWord(string text, bool mixed) => Assert.Equal(mixed, Homoglyphs.HasMixedWord(text));
}

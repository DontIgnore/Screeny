using System.Windows;
using Screeny.Ocr;

namespace Screeny.Tests.Ocr;

public class PersonalDataDetectorTests
{
    private static List<(PersonalDataKind Kind, string Text)> Find(string text)
        => PersonalDataDetector.Scan(text).Select(m => (m.Kind, text.Substring(m.Start, m.Length))).ToList();

    private static (PersonalDataKind Kind, string Text) Single(string text) => Assert.Single(Find(text));

    // ---------- Почта ----------

    [Theory]
    [InlineData("Email: john.doe@example.com", "john.doe@example.com")]
    [InlineData("write to a.b+tag@mail.co.uk, thanks", "a.b+tag@mail.co.uk")]
    [InlineData("support@screeny.page", "support@screeny.page")]
    public void Email_IsFound(string text, string expected)
        => Assert.Equal((PersonalDataKind.Email, expected), Single(text));

    [Theory]
    [InlineData("@username mentioned you")]
    [InlineData("price@ 5 dollars")]
    [InlineData("user@localhost")]
    public void NotAnEmail(string text) => Assert.Empty(Find(text));

    // ---------- Телефоны ----------

    [Theory]
    [InlineData("Phone: +1 (555) 123-4567", "+1 (555) 123-4567")]
    [InlineData("+7 999 123-45-67", "+7 999 123-45-67")]
    [InlineData("call +44 20 7946 0958 now", "+44 20 7946 0958")]
    [InlineData("+79991234567", "+79991234567")]
    [InlineData("(555) 123-4567", "(555) 123-4567")]
    [InlineData("555-123-4567", "555-123-4567")]
    [InlineData("8 800 555 35 35", "8 800 555 35 35")]
    [InlineData("1-800-555-0199", "1-800-555-0199")]
    public void Phone_IsFound(string text, string expected)
        => Assert.Equal((PersonalDataKind.Phone, expected), Single(text));

    [Theory]
    [InlineData("Released 2026-10-30")]
    [InlineData("30.10.2026 at 12:45:07")]
    [InlineData("1920 × 1080")]
    [InlineData("1920 x 1080 at 60 Hz")]
    [InlineData("Version 0.2.3")]
    [InlineData("Version 10.0.19041.0")]
    [InlineData("10 20 30 40 50")]
    [InlineData("100 200 300 400")]
    [InlineData("Total 1 299 000 ₽")]
    [InlineData("$1,299.00")]
    [InlineData("Order 2026103012345")]
    [InlineData("12:30 - 13:45")]
    [InlineData("page 12 of 345")]
    public void NotPersonalData(string text) => Assert.Empty(Find(text));

    // ---------- Карты ----------

    [Theory]
    [InlineData("Card: 4111 1111 1111 1111", "4111 1111 1111 1111")]
    [InlineData("5500-0000-0000-0004", "5500-0000-0000-0004")]
    [InlineData("378282246310005", "378282246310005")]
    [InlineData("4012888888881881", "4012888888881881")]
    public void Card_WithValidLuhn_IsFound(string text, string expected)
        => Assert.Equal((PersonalDataKind.Card, expected), Single(text));

    [Theory]
    [InlineData("4111 1111 1111 1112")]      // контрольная цифра не та
    [InlineData("0000 0000 0000 0000")]      // одна цифра
    [InlineData("1730265600000")]            // метка времени в миллисекундах
    public void Card_InvalidOrImplausible_IsNotFound(string text)
        => Assert.DoesNotContain(Find(text), m => m.Kind == PersonalDataKind.Card);

    [Theory]
    [InlineData("4111111111111111", true)]
    [InlineData("79927398713", true)]
    [InlineData("79927398710", false)]
    [InlineData("", false)]
    [InlineData("12a4", false)]
    public void Luhn(string digits, bool valid) => Assert.Equal(valid, PersonalDataDetector.Luhn(digits));

    // ---------- Ключи и токены ----------

    [Theory]
    [InlineData("Key: sk-proj-AbC123xYz789QweRtY456uIoPas", "sk-proj-AbC123xYz789QweRtY456uIoPas")]
    [InlineData("ghp_16C7e42F292c6912E7710c838347Ae178B4a", "ghp_16C7e42F292c6912E7710c838347Ae178B4a")]
    [InlineData("token github_pat_11ABCDEFG0abcdefghijkl_mnopqrstuvwxyz0123456789", "github_pat_11ABCDEFG0abcdefghijkl_mnopqrstuvwxyz0123456789")]
    [InlineData("AKIAIOSFODNN7EXAMPLE", "AKIAIOSFODNN7EXAMPLE")]
    [InlineData("xoxb-1234567890-abcdefghij", "xoxb-1234567890-abcdefghij")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U",
        "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U")]
    [InlineData("commit a94a8fe5ccb19ba61c4c0873d391e987982fbbd3", "a94a8fe5ccb19ba61c4c0873d391e987982fbbd3")]
    [InlineData("secret: q8Vz/3kLm9Pw2RtY+xB4nH7cJ1dF6gS0aZeU5iO", "q8Vz/3kLm9Pw2RtY+xB4nH7cJ1dF6gS0aZeU5iO")]
    [InlineData("dGhpcyBpcyBhIHNlY3JldCBrZXkgZm9yIHRlc3Rpbmc9", "dGhpcyBpcyBhIHNlY3JldCBrZXkgZm9yIHRlc3Rpbmc9")]
    public void Secret_IsFound(string text, string expected)
        => Assert.Equal((PersonalDataKind.Secret, expected), Single(text));

    [Theory]
    [InlineData("https://github.com/DontIgnore/Screeny/releases/tag/v0.2.3")]
    [InlineData(@"C:\Users\Someone\AppData\Local\DontIgnore.Screeny\current\Screeny.exe")]
    [InlineData("src/Screeny/Overlay/OverlayWindow.Privacy.cs")]
    [InlineData("Supercalifragilisticexpialidocious_and_more_words_here")]
    [InlineData("PersonalDataDetectorTests2ShouldNotMatchThisName")]
    [InlineData("sk-short")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa1111")]
    public void NotASecret(string text) => Assert.Empty(Find(text));

    // ---------- IP ----------

    [Theory]
    [InlineData("Server: 192.168.10.24", "192.168.10.24")]
    [InlineData("ping 8.8.8.8.", "8.8.8.8")]
    public void Ipv4_IsFound(string text, string expected)
        => Assert.Equal((PersonalDataKind.IpAddress, expected), Single(text));

    [Theory]
    [InlineData("999.168.10.24")]
    [InlineData("v1.2.3.4")]
    [InlineData("1.2.3.4.5")]
    public void NotAnIp(string text) => Assert.DoesNotContain(Find(text), m => m.Kind == PersonalDataKind.IpAddress);

    // ---------- Несколько видов и раскладка ----------

    [Fact]
    public void SeveralKinds_InOneLine_DoNotOverlap()
    {
        var found = Find("mail me@x.io or +1 555 123 4567, card 4111 1111 1111 1111, host 10.0.0.1");
        Assert.Equal([PersonalDataKind.Email, PersonalDataKind.Phone, PersonalDataKind.Card, PersonalDataKind.IpAddress], found.Select(f => f.Kind));
    }

    private static OcrWord W(string text, double x, double width = 40) => new(text, new Rect(x, 10, width, 20));

    [Fact]
    public void Line_SplitIntoWords_ReturnsUnionOfTouchedWordBoxes()
    {
        // OCR делит номер карты на четыре слова; рамка совпадения — от первого до последнего, без слова «Card:».
        var line = new OcrLine([W("Card:", 0), W("4111", 50), W("1111", 100), W("1111", 150), W("1111", 200), W("ok", 250)], "");
        var m = Assert.Single(PersonalDataDetector.Detect(line));
        Assert.Equal(PersonalDataKind.Card, m.Kind);
        Assert.Equal("4111 1111 1111 1111", m.Text);
        Assert.Equal(new Rect(50, 10, 190, 20), m.Box);
    }

    [Fact]
    public void Layout_MatchesAreReportedPerLine()
    {
        var layout = new OcrLayout([
            new OcrLine([W("Email:", 0, 60), W("john.doe@example.com", 70, 200)], ""),
            new OcrLine([W("Nothing", 0), W("here", 50)], ""),
            new OcrLine([W("Server:", 0, 60), W("192.168.10.24", 70, 120)], ""),
        ]);
        var all = PersonalDataDetector.Detect(layout);
        Assert.Equal([PersonalDataKind.Email, PersonalDataKind.IpAddress], all.Select(m => m.Kind));
        Assert.Equal(new Rect(70, 10, 200, 20), all[0].Box);
    }

    [Fact]
    public void EmptyLayout_FindsNothing() => Assert.Empty(PersonalDataDetector.Detect(OcrLayout.Empty));

    [Fact]
    public void Entropy_OfUniformAndRandomStrings()
    {
        Assert.Equal(0, PersonalDataDetector.Entropy("aaaa"));
        Assert.Equal(2, PersonalDataDetector.Entropy("abcd"), 6);
    }
}

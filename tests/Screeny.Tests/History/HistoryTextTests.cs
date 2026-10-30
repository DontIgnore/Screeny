using Screeny.History;

namespace Screeny.Tests.History;

/// <summary>Формат sidecar-файла распознанного текста <c>&lt;stamp&gt;.text.json</c>.</summary>
public class HistoryTextTests
{
    private static readonly HistoryTextMarker Marker = new("windows-ocr", 1, ["en-US", "ru"]);

    [Fact]
    public void RoundTrip_KeepsTextAndMarker()
    {
        var original = new HistoryText("Ошибка 0x80070005\r\n\"Access denied\" — 日本語", Marker);
        var parsed = HistoryText.TryParse(original.Serialize())!;
        Assert.Equal(original.Text, parsed.Text);
        Assert.Equal("windows-ocr", parsed.Marker.Engine);
        Assert.Equal(1, parsed.Marker.Version);
        Assert.Equal(["en-US", "ru"], parsed.Marker.Languages);
    }

    [Fact]
    public void EmptyText_IsAValidRecord()
        => Assert.Equal("", HistoryText.TryParse(new HistoryText("", Marker).Serialize())!.Text);

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"version\":1}")]
    [InlineData("{\"version\":2,\"text\":\"x\"}")]
    [InlineData("{\"version\":0,\"text\":\"x\"}")]
    [InlineData("{\"version\":1.5,\"text\":\"x\"}")]
    [InlineData("{\"version\":1,\"text\":5}")]
    public void Broken_OrNewerFormat_IsNull(string json) => Assert.Null(HistoryText.TryParse(json));

    [Fact]
    public void MissingMarkerFields_ParseAsUnknownMarker()
    {
        var text = HistoryText.TryParse("{\"version\":1,\"text\":\"x\",\"future\":{\"a\":1}}")!;
        Assert.Equal("x", text.Text);
        Assert.False(text.Marker.SameAs(Marker)); // такой снимок распознаётся заново
    }

    [Fact]
    public void Marker_ComparesLanguagesAsASet()
    {
        Assert.True(Marker.SameAs(new HistoryTextMarker("Windows-OCR", 1, ["RU", "en-us"])));
        Assert.False(Marker.SameAs(new HistoryTextMarker("windows-ocr", 1, ["en-US"])));
        Assert.False(Marker.SameAs(new HistoryTextMarker("windows-ocr", 2, ["en-US", "ru"])));
        Assert.False(Marker.SameAs(new HistoryTextMarker("paddle", 1, ["en-US", "ru"])));
        Assert.False(Marker.SameAs(null));
    }

    [Fact]
    public void TryRead_MissingFile_IsNull()
    {
        Assert.Null(HistoryText.TryRead(null));
        Assert.Null(HistoryText.TryRead(Path.Combine(Path.GetTempPath(), "ScreenyTests", Guid.NewGuid().ToString("N"), "x.text.json")));
    }
}

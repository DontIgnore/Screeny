using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;
using Screeny.Export;

namespace Screeny.Tests.Annotations;

public class AnnotationSerializerTests
{
    private static readonly AnnotationStyle Styled = new(Color.FromRgb(0x30, 0x78, 0xF6), 5)
    {
        StrokeEnabled = false, Fill = Color.FromArgb(0x66, 0xFF, 0x3B, 0x30), Arrow = ArrowStyle.Thick, TextBackground = Colors.Black,
    };

    private static AnnotationFile RoundTrip(Annotation a, Point origin = default)
        => AnnotationSerializer.Deserialize(AnnotationSerializer.Serialize([a], origin, 300, 200, null));

    [Theory]
    [InlineData("rect")]
    [InlineData("ellipse")]
    [InlineData("pixelate")]
    [InlineData("blur")]
    public void RoundTrip_BoxTypes_KeepRectStyleAndType(string type)
    {
        var rect = new Rect(10.5, 20, 100, 50.25);
        Annotation a = type switch
        {
            "rect" => new RectAnnotation(rect, Styled),
            "ellipse" => new EllipseAnnotation(rect, Styled),
            "pixelate" => new PixelateAnnotation(rect, Styled),
            _ => new BlurAnnotation(rect, Styled),
        };
        var back = Assert.Single(RoundTrip(a).Annotations);
        Assert.Equal(a.GetType(), back.GetType());
        Assert.Equal(rect, ((BoxAnnotation)back).Rect);
        Assert.Equal(Styled, back.Style);
    }

    [Fact]
    public void RoundTrip_LineAndArrow_KeepEndsAndExactType()
    {
        var line = Assert.IsType<LineAnnotation>(Assert.Single(RoundTrip(new LineAnnotation(new Point(1, 2), new Point(30, 40), Styled)).Annotations));
        Assert.Equal((new Point(1, 2), new Point(30, 40)), (line.Start, line.End));

        var arrow = Assert.IsType<ArrowAnnotation>(Assert.Single(RoundTrip(new ArrowAnnotation(new Point(5, 6), new Point(70, 80), Styled)).Annotations));
        Assert.Equal((new Point(5, 6), new Point(70, 80)), (arrow.Start, arrow.End));
        Assert.Equal(ArrowStyle.Thick, arrow.Style.Arrow);
    }

    [Fact]
    public void RoundTrip_PenAndHighlighter_KeepAllPoints()
    {
        var pen = new PenAnnotation(new Point(1, 1), Styled);
        pen.AddPoint(new Point(5, 7)); pen.AddPoint(new Point(9.5, 3));
        var back = Assert.IsType<PenAnnotation>(Assert.Single(RoundTrip(pen).Annotations));
        Assert.Equal(pen.Points, back.Points);
        Assert.Equal(Styled, back.Style);

        var marker = new HighlighterAnnotation(new Point(2, 2), Styled);
        marker.AddPoint(new Point(40, 2));
        var mb = Assert.IsType<HighlighterAnnotation>(Assert.Single(RoundTrip(marker).Annotations));
        Assert.Equal(marker.Points, mb.Points);
    }

    [Fact]
    public void RoundTrip_SinglePointPen_StaysAPoint()
    {
        var back = Assert.IsType<PenAnnotation>(Assert.Single(RoundTrip(new PenAnnotation(new Point(3, 4), Styled)).Annotations));
        Assert.Equal([new Point(3, 4)], back.Points);
    }

    [Fact]
    public void RoundTrip_TextAndNumber()
    {
        var text = Assert.IsType<TextAnnotation>(Assert.Single(RoundTrip(new TextAnnotation(new Point(7, 8), "Привет\n\"мир\"", Styled)).Annotations));
        Assert.Equal((new Point(7, 8), "Привет\n\"мир\""), (text.Origin, text.Text));
        Assert.Equal(Styled, text.Style);

        var number = Assert.IsType<NumberAnnotation>(Assert.Single(RoundTrip(new NumberAnnotation(new Point(50, 60), 12, Styled)).Annotations));
        Assert.Equal((new Point(50, 60), 12), (number.Center, number.Number));
    }

    [Fact]
    public void Serialize_ShiftsCoordinatesByRegionOrigin()
    {
        var origin = new Point(100, 40);
        var file = AnnotationSerializer.Deserialize(AnnotationSerializer.Serialize(
            [new RectAnnotation(new Rect(110, 50, 20, 10), Styled), new LineAnnotation(new Point(100, 40), new Point(90, 30), Styled),
             new NumberAnnotation(new Point(150, 90), 1, Styled)], origin, 300, 200, null));
        Assert.Equal(new Rect(10, 10, 20, 10), ((RectAnnotation)file.Annotations[0]).Rect);
        var line = (LineAnnotation)file.Annotations[1];
        Assert.Equal(new Point(0, 0), line.Start);
        Assert.Equal(new Point(-10, -10), line.End); // outside region — preserved as-is
        Assert.Equal(new Point(50, 50), ((NumberAnnotation)file.Annotations[2]).Center);
    }

    [Fact]
    public void Serialize_WritesVersionSizeBeautifyAndOrder()
    {
        var beautify = new BeautifyOptions(BeautifyPreset.Sunset, BeautifyPadding.L, 16, false);
        var json = AnnotationSerializer.Serialize(
            [new RectAnnotation(new Rect(0, 0, 5, 5), Styled), new BlurAnnotation(new Rect(1, 1, 5, 5), Styled)], default, 640, 480, beautify);
        var node = JsonNode.Parse(json)!;
        Assert.Equal(1, (int)node["version"]!);
        Assert.Equal(640, (int)node["size"]!["w"]!);
        Assert.Equal(480, (int)node["size"]!["h"]!);
        Assert.Equal(["rect", "blur"], node["annotations"]!.AsArray().Select(a => (string)a!["type"]!));

        var file = AnnotationSerializer.Deserialize(json);
        Assert.Equal((640, 480), (file.Width, file.Height));
        Assert.Equal(beautify, file.Beautify);
    }

    [Fact]
    public void Serialize_DocumentIgnoresLiveShape()
    {
        var doc = new AnnotationDocument();
        doc.Add(new RectAnnotation(new Rect(0, 0, 5, 5), Styled));
        doc.Live = new EllipseAnnotation(new Rect(0, 0, 9, 9), Styled);
        var file = AnnotationSerializer.Deserialize(AnnotationSerializer.Serialize(doc, default, 10, 10, null));
        Assert.IsType<RectAnnotation>(Assert.Single(file.Annotations));
        Assert.Null(file.Beautify);
    }

    [Fact]
    public void Deserialize_SkipsUnknownType_KeepsOthers()
    {
        var node = JsonNode.Parse(AnnotationSerializer.Serialize(
            [new RectAnnotation(new Rect(0, 0, 5, 5), Styled), new EllipseAnnotation(new Rect(1, 1, 5, 5), Styled)], default, 10, 10, null))!;
        node["annotations"]![0]!["type"] = "hexagon";
        var file = AnnotationSerializer.Deserialize(node.ToJsonString());
        Assert.IsType<EllipseAnnotation>(Assert.Single(file.Annotations));
        Assert.Equal(1, file.Skipped);
    }

    [Fact]
    public void Deserialize_UnknownTypeWithoutStyle_SkipsIt()
    {
        const string json = "{\"version\":1,\"size\":{\"w\":10,\"h\":10},\"annotations\":[{\"type\":\"future-shape\"}]}";
        var file = AnnotationSerializer.Deserialize(json);
        Assert.Empty(file.Annotations);
        Assert.Equal(1, file.Skipped);
    }

    [Theory]
    [InlineData("1e400", "0")]
    [InlineData("0", "-1e400")]
    public void Deserialize_PenWithNonFiniteCoordinates_ThrowsFormatException(string x, string y)
    {
        var json = $"{{\"version\":1,\"size\":{{\"w\":10,\"h\":10}},\"annotations\":[{{\"type\":\"pen\",\"style\":{{}},\"points\":[[{x},{y}]]}}]}}";
        Assert.Throws<FormatException>(() => AnnotationSerializer.Deserialize(json));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("{\"size\":{\"w\":1,\"h\":1},\"annotations\":[]}")]                                     // no version
    [InlineData("{\"version\":1,\"annotations\":[]}")]                                                  // no size
    [InlineData("{\"version\":1,\"size\":{\"w\":1,\"h\":1}}")]                                          // no annotations
    [InlineData("{\"version\":\"1\",\"size\":{\"w\":1,\"h\":1},\"annotations\":[]}")]                    // version string
    [InlineData("{\"version\":1,\"size\":{\"w\":0,\"h\":1},\"annotations\":[]}")]                        // empty size
    [InlineData("{\"version\":1,\"size\":{\"w\":1,\"h\":1},\"annotations\":[{\"type\":\"rect\",\"style\":{\"color\":\"#FFFF0000\"},\"x\":1}]}")]   // no geometry
    [InlineData("{\"version\":1,\"size\":{\"w\":1,\"h\":1},\"annotations\":[{\"type\":\"rect\",\"style\":{\"color\":\"nope\"},\"x\":1,\"y\":1,\"w\":1,\"h\":1}]}")] // broken style
    [InlineData("{\"version\":1,\"size\":{\"w\":1,\"h\":1},\"annotations\":[{\"type\":\"pen\",\"style\":{},\"points\":[]}]}")]  // stroke without points
    [InlineData("{\"version\":1,\"size\":{\"w\":1,\"h\":1},\"beautify\":{\"preset\":\"Neon\",\"padding\":\"M\",\"radius\":8,\"shadow\":true},\"annotations\":[]}")]
    public void Deserialize_BrokenJson_ThrowsFormatException(string json)
        => Assert.Throws<FormatException>(() => AnnotationSerializer.Deserialize(json));

    [Theory]
    [InlineData(2)]
    [InlineData(0)]
    public void Deserialize_UnsupportedVersion_ThrowsFormatException(int version)
    {
        var node = JsonNode.Parse(AnnotationSerializer.Serialize([], default, 10, 10, null))!;
        node["version"] = version;
        var ex = Assert.ThrowsAny<FormatException>(() => AnnotationSerializer.Deserialize(node.ToJsonString()));
        Assert.Contains(version.ToString(), ex.Message);
        // Document from newer version — separate type: editor tells user its version.
        Assert.Equal(version > AnnotationSerializer.CurrentVersion, ex is UnsupportedDocumentVersionException);
    }
}

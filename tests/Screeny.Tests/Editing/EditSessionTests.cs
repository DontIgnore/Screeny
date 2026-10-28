using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;
using Screeny.Editing;
using Screeny.Export;
using Screeny.History;

namespace Screeny.Tests.Editing;

public class EditSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScreenyTests", Guid.NewGuid().ToString("N"));
    private static readonly AnnotationStyle Red = new(Colors.Red, 3);

    public EditSessionTests() => Directory.CreateDirectory(_dir);

    private static byte[] Png(int w, int h, byte gray, double dpi = 96)
    {
        var px = new byte[w * h * 4];
        for (int i = 0; i < px.Length; i += 4) { px[i] = px[i + 1] = px[i + 2] = gray; px[i + 3] = 255; }
        return ClipboardService.EncodePng(BitmapSource.Create(w, h, dpi, dpi, PixelFormats.Bgra32, null, px, w * 4));
    }

    private static Color PixelAt(BitmapSource bmp, int x, int y)
    {
        var px = new byte[4];
        new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0).CopyPixels(new Int32Rect(x, y, 1, 1), px, 4, 0);
        return Color.FromArgb(px[3], px[2], px[1], px[0]);
    }

    /// <summary>Запись истории: готовый PNG (серый 200), при желании оригинал (серый 50) и JSON.</summary>
    private string Entry(bool original, string? json)
    {
        var store = new HistoryStore(_dir, 10);
        var orig = original ? Png(40, 30, 50) : null;
        return store.Save(Png(40, 30, 200), new HistorySidecar(json, orig));
    }

    private static string Json(params Annotation[] items) => AnnotationSerializer.Serialize(items, default, 40, 30, null);

    [Fact]
    public void Load_WithOriginalAndJson_UsesOriginalAndAnnotations()
    {
        var beautify = new BeautifyOptions(BeautifyPreset.Mint, BeautifyPadding.S, 8, true);
        var path = Entry(true, AnnotationSerializer.Serialize([new RectAnnotation(new Rect(2, 2, 10, 10), Red)], default, 40, 30, beautify));
        var s = EditSession.Load(path);
        Assert.True(s.HasOriginal);
        Assert.Equal(Color.FromRgb(50, 50, 50), PixelAt(s.Base, 20, 20));
        Assert.Equal(PixelFormats.Bgra32, s.Base.Format);
        Assert.True(s.Base.IsFrozen);
        Assert.IsType<RectAnnotation>(Assert.Single(s.Annotations));
        Assert.Equal(beautify, s.Beautify);
        Assert.NotNull(s.OriginalPng);
        Assert.Null(s.Message);
    }

    [Fact]
    public void Load_WithoutOriginal_UsesFinishedPng_IgnoresJson()
    {
        var path = Entry(false, Json(new RectAnnotation(new Rect(2, 2, 10, 10), Red)));
        var s = EditSession.Load(path);
        Assert.False(s.HasOriginal);
        Assert.Equal(Color.FromRgb(200, 200, 200), PixelAt(s.Base, 20, 20));
        Assert.Empty(s.Annotations);
        Assert.Null(s.Beautify);
        Assert.Null(s.Message);
    }

    [Theory]
    [InlineData("{ broken")]
    [InlineData(null)]
    public void Load_BrokenOrMissingJson_FallsBackToFinishedPng_WithMessage(string? json)
    {
        var s = EditSession.Load(Entry(true, json));
        Assert.False(s.HasOriginal);
        Assert.Equal(Color.FromRgb(200, 200, 200), PixelAt(s.Base, 20, 20));
        Assert.Empty(s.Annotations);
        Assert.NotNull(s.Message);
        // Причина — понятным текстом на языке интерфейса (тесты — по-русски), без внутренних подробностей и двойной точки.
        Assert.StartsWith(json is null ? "Документ пометок не найден. " : "Документ пометок повреждён. ", s.Message);
        Assert.DoesNotContain("..", s.Message);
        Assert.DoesNotContain("Corrupt", s.Message);
    }

    [Fact]
    public void Load_NewerVersion_FallsBackWithMessage()
    {
        var node = JsonNode.Parse(Json())!;
        node["version"] = 2;
        var s = EditSession.Load(Entry(true, node.ToJsonString()));
        Assert.False(s.HasOriginal);
        Assert.StartsWith("Документ пометок версии 2 — из более новой версии Screeny. ", s.Message);
    }

    [Fact]
    public void Load_SizeMismatch_FallsBackWithMessage()
    {
        var s = EditSession.Load(Entry(true, AnnotationSerializer.Serialize([], default, 41, 30, null)));
        Assert.False(s.HasOriginal);
        Assert.NotNull(s.Message);
    }

    [Fact]
    public void Load_InvalidThickness_FallsBackToFinishedImage()
    {
        var node = JsonNode.Parse(Json(new RectAnnotation(new Rect(1, 1, 5, 5), Red)))!;
        node["annotations"]![0]!["style"]!["thickness"] = -1;
        var s = EditSession.Load(Entry(true, node.ToJsonString()));
        Assert.False(s.HasOriginal);
        Assert.Equal(Color.FromRgb(200, 200, 200), PixelAt(s.Base, 20, 20));
        Assert.NotNull(s.Message);
    }

    [Fact]
    public void Load_UnknownType_UsesFinishedImageToPreserveAllEffects()
    {
        var node = JsonNode.Parse(Json(new RectAnnotation(new Rect(1, 1, 5, 5), Red), new EllipseAnnotation(new Rect(1, 1, 5, 5), Red)))!;
        node["annotations"]![0]!["type"] = "star";
        var s = EditSession.Load(Entry(true, node.ToJsonString()));
        Assert.False(s.HasOriginal);
        Assert.Equal(Color.FromRgb(200, 200, 200), PixelAt(s.Base, 20, 20));
        Assert.Empty(s.Annotations);
        Assert.Contains("1", s.Message);
    }

    [Fact]
    public void Decode_NormalizesDpiAndFormat()
    {
        var bmp = EditSession.Decode(Png(10, 8, 10, dpi: 144));
        Assert.Equal((10, 8, 96.0), (bmp.PixelWidth, bmp.PixelHeight, bmp.DpiX));
        Assert.Equal(PixelFormats.Bgra32, bmp.Format);
    }

    [Fact]
    public void Build_ComposesVisibleAnnotations_AndMatchingJson()
    {
        var s = EditSession.Load(Entry(true, Json()));
        var inside = new RectAnnotation(new Rect(5, 5, 10, 10), new AnnotationStyle(Colors.Red, 2) { Fill = Colors.Red });
        var outside = new RectAnnotation(new Rect(100, 100, 5, 5), Red);
        var r = EditResult.Build(s, [inside, outside], null, keepOriginals: true);

        Assert.Equal((40, 30), (r.Image.PixelWidth, r.Image.PixelHeight));
        Assert.Equal(Colors.Red, PixelAt(r.Image, 10, 10));
        Assert.Equal(Color.FromRgb(50, 50, 50), PixelAt(r.Image, 30, 25)); // основа — оригинал
        var file = AnnotationSerializer.Deserialize(r.AnnotationsJson);
        Assert.Single(file.Annotations); // за пределами снимка — не сохраняется
        Assert.Equal(s.OriginalPng, r.OriginalPng);
    }

    [Fact]
    public void Build_OriginalMode_EmptyJson_AndBeautify()
    {
        var s = EditSession.Load(Entry(true, Json(new RectAnnotation(new Rect(2, 2, 10, 10), Red))));
        var beautify = new BeautifyOptions(BeautifyPreset.Blue, BeautifyPadding.S, 0, false);
        var r = EditResult.Build(s, [], beautify, keepOriginals: true);
        Assert.Equal((40 + 48, 30 + 48), (r.Image.PixelWidth, r.Image.PixelHeight));
        var file = AnnotationSerializer.Deserialize(r.AnnotationsJson);
        Assert.Empty(file.Annotations);
        Assert.Equal(beautify, file.Beautify);
    }

    [Fact]
    public void Build_CopiesOriginalOnlyWhenSettingOnAndSourceHadIt()
    {
        var withOrig = EditSession.Load(Entry(true, Json()));
        Assert.Null(EditResult.Build(withOrig, [], null, keepOriginals: false).OriginalPng);
        var flat = EditSession.Load(Entry(false, null));
        Assert.Null(EditResult.Build(flat, [], null, keepOriginals: true).OriginalPng);
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using Screeny.Annotations.Shapes;
using Screeny.Export;
using Screeny.Logging;

namespace Screeny.Annotations;

/// <summary>Document read from JSON: annotations (already in region coordinates), region size, decoration.</summary>
/// <param name="Skipped">How many annotations of unknown type were skipped (file from a newer version).</param>
public sealed record AnnotationFile(IReadOnlyList<Annotation> Annotations, int Width, int Height, BeautifyOptions? Beautify, int Skipped = 0);

/// <summary>
/// Annotation document JSON for re-editing from history (<c>&lt;stamp&gt;.screeny.json</c>):
/// <code>
/// { "version": 1, "size": { "w": 900, "h": 560 }, "beautify": { ... } | null,
///   "annotations": [ { "type": "rect", "style": { ... }, "x": 10, "y": 20, "w": 100, "h": 50 }, ... ] }
/// </code>
/// Coordinates are in screenshot pixels relative to the region top-left corner. Geometry by type:
/// <c>rect|ellipse|pixelate|blur</c> — x, y, w, h; <c>line|arrow</c> — x1, y1, x2, y2; <c>pen|highlighter</c> — points [[x, y], …];
/// <c>text</c> — x, y, text; <c>number</c> — x, y (center), n. Style — <see cref="AnnotationStyleJsonConverter"/>.
/// <para>Unknown <c>type</c> is skipped (logged). Malformed JSON, missing required field or
/// <c>version</c> newer than supported — <see cref="FormatException"/>: caller opens the screenshot without annotations.</para>
/// </summary>
/// <summary>Marks document from a newer Screeny: its version is higher than supported.</summary>
public sealed class UnsupportedDocumentVersionException(int version) : FormatException($"Unsupported annotation document version: {version}.")
{
    public int Version { get; } = version;
}

public static class AnnotationSerializer
{
    public const int CurrentVersion = 1;

    public static string Serialize(AnnotationDocument doc, Point regionOrigin, int width, int height, BeautifyOptions? beautify)
        => Serialize(doc.Items, regionOrigin, width, height, beautify);

    /// <param name="regionOrigin">Top-left corner of region in annotation coordinates: it becomes (0, 0).</param>
    public static string Serialize(IEnumerable<Annotation> annotations, Point regionOrigin, int width, int height, BeautifyOptions? beautify)
    {
        var offset = new Vector(-regionOrigin.X, -regionOrigin.Y);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", CurrentVersion);
            w.WriteStartObject("size");
            w.WriteNumber("w", width);
            w.WriteNumber("h", height);
            w.WriteEndObject();
            if (beautify is null) w.WriteNull("beautify");
            else WriteBeautify(w, beautify);
            w.WriteStartArray("annotations");
            foreach (var a in annotations)
            {
                // Order matters: subclasses (arrow, marker) before base types.
                if (TypeName(a) is not { } type) { Log.Error($"Annotation type {a.GetType().Name} is not serializable; skipped"); continue; }
                w.WriteStartObject();
                w.WriteString("type", type);
                w.WritePropertyName("style");
                JsonSerializer.Serialize(w, a.Style);
                WriteGeometry(w, a, offset);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private static string? TypeName(Annotation a) => a switch
    {
        ArrowAnnotation => "arrow",
        LineAnnotation => "line",
        HighlighterAnnotation => "highlighter",
        PenAnnotation => "pen",
        RectAnnotation => "rect",
        EllipseAnnotation => "ellipse",
        PixelateAnnotation => "pixelate",
        BlurAnnotation => "blur",
        TextAnnotation => "text",
        NumberAnnotation => "number",
        _ => null,
    };

    private static void WriteGeometry(Utf8JsonWriter w, Annotation a, Vector offset)
    {
        switch (a)
        {
            case LineAnnotation line:
                var s = line.Start + offset; var e = line.End + offset;
                w.WriteNumber("x1", s.X); w.WriteNumber("y1", s.Y); w.WriteNumber("x2", e.X); w.WriteNumber("y2", e.Y);
                break;
            case PenAnnotation pen:
                w.WriteStartArray("points");
                foreach (var p0 in pen.Points)
                {
                    var p = p0 + offset;
                    w.WriteStartArray(); w.WriteNumberValue(p.X); w.WriteNumberValue(p.Y); w.WriteEndArray();
                }
                w.WriteEndArray();
                break;
            case BoxAnnotation box:
                var r = box.Rect;
                w.WriteNumber("x", r.X + offset.X); w.WriteNumber("y", r.Y + offset.Y); w.WriteNumber("w", r.Width); w.WriteNumber("h", r.Height);
                break;
            case TextAnnotation text:
                var o = text.Origin + offset;
                w.WriteNumber("x", o.X); w.WriteNumber("y", o.Y); w.WriteString("text", text.Text);
                break;
            case NumberAnnotation number:
                var c = number.Center + offset;
                w.WriteNumber("x", c.X); w.WriteNumber("y", c.Y); w.WriteNumber("n", number.Number);
                break;
        }
    }

    private static void WriteBeautify(Utf8JsonWriter w, BeautifyOptions b)
    {
        w.WriteStartObject("beautify");
        w.WriteString("preset", b.Preset.ToString());
        w.WriteString("padding", b.Padding.ToString());
        w.WriteNumber("radius", b.Radius);
        w.WriteBoolean("shadow", b.Shadow);
        w.WriteEndObject();
    }

    /// <exception cref="FormatException">Malformed JSON, missing required field, invalid value or unsupported version.</exception>
    public static AnnotationFile Deserialize(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new FormatException("Document root must be an object.");
            int version = Required(root, "version").GetInt32();
            if (version < 1) throw new FormatException($"Invalid annotation document version: {version}.");
            if (version > CurrentVersion) throw new UnsupportedDocumentVersionException(version);
            var size = Required(root, "size");
            int width = Required(size, "w").GetInt32(), height = Required(size, "h").GetInt32();
            if (width <= 0 || height <= 0) throw new FormatException("Invalid document size.");

            BeautifyOptions? beautify = null;
            if (root.TryGetProperty("beautify", out var b) && b.ValueKind != JsonValueKind.Null) beautify = ReadBeautify(b);

            var list = new List<Annotation>();
            int skipped = 0;
            var items = Required(root, "annotations");
            if (items.ValueKind != JsonValueKind.Array) throw new FormatException("annotations is not an array.");
            foreach (var item in items.EnumerateArray())
            {
                var type = Required(item, "type").GetString();
                if (Read(item, type) is { } annotation) list.Add(annotation);
                else
                {
                    skipped++;
                    Log.Error($"Unknown annotation type '{type}' in document; skipped");
                }
            }
            return new AnnotationFile(list, width, height, beautify, skipped);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        {
            // JsonException — syntax and style, InvalidOperationException — value of wrong type (GetDouble on string).
            throw new FormatException($"Corrupt annotation document: {ex.Message}", ex);
        }
    }

    private static Annotation? Read(JsonElement e, string? type)
    {
        AnnotationStyle Style() => Required(e, "style").Deserialize<AnnotationStyle>()
                                   ?? throw new FormatException("Empty annotation style.");
        switch (type)
        {
            case "rect": return new RectAnnotation(ReadRect(e), Style());
            case "ellipse": return new EllipseAnnotation(ReadRect(e), Style());
            case "pixelate": return new PixelateAnnotation(ReadRect(e), Style());
            case "blur": return new BlurAnnotation(ReadRect(e), Style());
            case "line": return new LineAnnotation(ReadPoint(e, "x1", "y1"), ReadPoint(e, "x2", "y2"), Style());
            case "arrow": return new ArrowAnnotation(ReadPoint(e, "x1", "y1"), ReadPoint(e, "x2", "y2"), Style());
            case "pen":
            case "highlighter":
            {
                var points = ReadPoints(e);
                var style = Style();
                PenAnnotation pen = type == "pen" ? new PenAnnotation(points[0], style) : new HighlighterAnnotation(points[0], style);
                for (int i = 1; i < points.Count; i++) pen.AddPoint(points[i]);
                return pen;
            }
            case "text":
                return new TextAnnotation(ReadPoint(e, "x", "y"), Required(e, "text").GetString() ?? "", Style());
            case "number":
                return new NumberAnnotation(ReadPoint(e, "x", "y"), Required(e, "n").GetInt32(), Style());
            default:
                return null;
        }
    }

    private static Rect ReadRect(JsonElement e)
    {
        double w = Number(e, "w"), h = Number(e, "h");
        if (w < 0 || h < 0) throw new FormatException("Negative shape size.");
        return new Rect(Number(e, "x"), Number(e, "y"), w, h);
    }

    private static Point ReadPoint(JsonElement e, string x, string y) => new(Number(e, x), Number(e, y));

    private static List<Point> ReadPoints(JsonElement e)
    {
        var arr = Required(e, "points");
        var list = new List<Point>();
        foreach (var p in arr.EnumerateArray())
        {
            if (p.GetArrayLength() != 2) throw new FormatException("A stroke point must be a pair of numbers.");
            list.Add(new Point(FiniteNumber(p[0]), FiniteNumber(p[1])));
        }
        if (list.Count == 0) throw new FormatException("Stroke has no points.");
        return list;
    }

    private static double Number(JsonElement e, string name)
        => FiniteNumber(Required(e, name));

    private static double FiniteNumber(JsonElement e)
    {
        double v = e.GetDouble();
        return double.IsFinite(v) ? v : throw new FormatException("Invalid number.");
    }

    private static JsonElement Required(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
            ? v
            : throw new FormatException($"Missing required field \"{name}\".");

    private static BeautifyOptions ReadBeautify(JsonElement b)
    {
        var preset = ParseEnum<BeautifyPreset>(Required(b, "preset").GetString());
        var padding = ParseEnum<BeautifyPadding>(Required(b, "padding").GetString());
        return new BeautifyOptions(preset, padding, BeautifyOptions.SnapRadius(Required(b, "radius").GetInt32()), Required(b, "shadow").GetBoolean());
    }

    private static T ParseEnum<T>(string? text) where T : struct, Enum
        => Enum.TryParse<T>(text, ignoreCase: true, out var v) && Enum.IsDefined(v) && !int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)
            ? v
            : throw new FormatException($"Invalid value \"{text}\".");
}

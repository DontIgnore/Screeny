using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Screeny.Annotations;

/// <summary>Arrow style.</summary>
public enum ArrowStyle
{
    /// <summary>Line with solid arrowhead at end.</summary>
    Normal,
    /// <summary>Arrowheads on both sides.</summary>
    Double,
    /// <summary>Dashed line with solid arrowhead.</summary>
    Dashed,
    /// <summary>“Banner” arrow: wedge body widening from start to wide arrowhead.</summary>
    Thick,
}

/// <summary>Alignment of multiline text lines within its block (block top-left corner is anchor).</summary>
public enum TextAlign
{
    Left,
    Center,
    Right,
}

/// <summary>
/// Immutable annotation style. Value equality: brush and pen caches in <see cref="Annotation"/>
/// are reset only on actual style change, and <see cref="AnnotationDocument.Restyle"/> does not record empty steps.
/// <para>
/// <see cref="Color"/> is the main color: shape stroke, line color, pencil, text, label.
/// Additional fields are used only by the shapes they apply to; for other tools
/// they remain default (see <see cref="Screeny.Overlay.StyleOptions.StyleFor"/>).
/// </para>
/// <para>Serialized to JSON (<see cref="AnnotationStyleJsonConverter"/>): colors — <c>"#AARRGGBB"</c>, arrow style — string.</para>
/// </summary>
[JsonConverter(typeof(AnnotationStyleJsonConverter))]
public sealed record AnnotationStyle(Color Color, double Thickness)
{
    /// <summary>Whether to draw stroke (rectangle, ellipse). Can be disabled only if fill exists.</summary>
    public bool StrokeEnabled { get; init; } = true;

    /// <summary>Fill for rectangle/ellipse with opacity (alpha); null — no fill.</summary>
    public Color? Fill { get; init; }

    public ArrowStyle Arrow { get; init; } = ArrowStyle.Normal;

    /// <summary>Rounded background behind text; null — no background.</summary>
    public Color? TextBackground { get; init; }

    /// <summary>Alignment of text lines within block; block does not move when alignment changes.</summary>
    public TextAlign TextAlign { get; init; } = TextAlign.Left;

    /// <summary>Stroke color or null if stroke is off.</summary>
    public Color? StrokeColor => StrokeEnabled ? Color : null;

    /// <summary>Toggles stroke. Cannot disable the last visible part of a shape — style unchanged.</summary>
    public AnnotationStyle WithStroke(bool enabled) => !enabled && Fill is null ? this : this with { StrokeEnabled = enabled };

    /// <summary>Sets or removes fill. Cannot remove fill from a shape without stroke — style unchanged.</summary>
    public AnnotationStyle WithFill(Color? fill) => fill is null && !StrokeEnabled ? this : this with { Fill = fill };

    /// <summary>Whether stroke can be disabled (fill remains).</summary>
    public bool CanDisableStroke => Fill is not null;

    /// <summary>Whether fill can be removed (stroke remains).</summary>
    public bool CanDisableFill => StrokeEnabled;
}

/// <summary>Color ↔ text.</summary>
public static class ColorHex
{
    /// <summary><c>#RRGGBB</c> — like in magnifier and clipboard (eyedropper). Alpha is discarded.</summary>
    public static string Rgb(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary><c>#AARRGGBB</c> — for JSON where transparency matters (40% fill).</summary>
    public static string Argb(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Parses <c>#RRGGBB</c>, <c>#AARRGGBB</c> and WPF color names; false — not a color.</summary>
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            if (ColorConverter.ConvertFromString(text) is not Color c) return false;
            color = c;
            return true;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException) { return false; }
    }

    /// <summary>Same color with opacity in percent (0…100).</summary>
    public static Color WithOpacity(Color c, int percent)
        => Color.FromArgb((byte)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100.0), c.R, c.G, c.B);

    /// <summary>Color opacity in percent rounded to integer.</summary>
    public static int OpacityPercent(Color c) => (int)Math.Round(c.A * 100 / 255.0);
}

/// <summary>
/// Style JSON: <c>{ "color": "#FFFF3B30", "thickness": 3, "stroke": true, "fill": "#66FF3B30" | null,
/// "arrow": "Normal", "textBackground": null, "textAlign": "Left" }</c>. Unknown fields are skipped, missing fields —
/// default (documents without <c>textAlign</c> — left alignment), invalid color, arrow style or alignment —
/// <see cref="JsonException"/>.
/// </summary>
public sealed class AnnotationStyleJsonConverter : JsonConverter<AnnotationStyle>
{
    public override AnnotationStyle Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Style must be an object.");
        Color color = Colors.Red;
        double thickness = 3;
        bool stroke = true;
        Color? fill = null, textBackground = null;
        var arrow = ArrowStyle.Normal;
        var align = TextAlign.Left;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException();
            var name = reader.GetString();
            reader.Read();
            switch (name?.ToLowerInvariant())
            {
                case "color": color = ReadColor(ref reader) ?? color; break;
                case "thickness":
                    thickness = reader.GetDouble();
                    if (!double.IsFinite(thickness) || thickness <= 0) throw new JsonException("Invalid thickness.");
                    break;
                case "stroke": stroke = reader.GetBoolean(); break;
                case "fill": fill = ReadColor(ref reader); break;
                case "textbackground": textBackground = ReadColor(ref reader); break;
                case "arrow":
                    if (!Enum.TryParse(reader.GetString(), ignoreCase: true, out arrow) || !Enum.IsDefined(arrow))
                        throw new JsonException("Unknown arrow style.");
                    break;
                case "textalign":
                    if (!Enum.TryParse(reader.GetString(), ignoreCase: true, out align) || !Enum.IsDefined(align))
                        throw new JsonException("Unknown text alignment.");
                    break;
                default: reader.Skip(); break;
            }
        }
        // Do not restore invisible shape: without fill stroke is required.
        return new AnnotationStyle(color, thickness) { StrokeEnabled = stroke || fill is null, Fill = fill, Arrow = arrow, TextBackground = textBackground, TextAlign = align };
    }

    public override void Write(Utf8JsonWriter writer, AnnotationStyle value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("color", ColorHex.Argb(value.Color));
        writer.WriteNumber("thickness", value.Thickness);
        writer.WriteBoolean("stroke", value.StrokeEnabled);
        WriteColor(writer, "fill", value.Fill);
        writer.WriteString("arrow", value.Arrow.ToString());
        WriteColor(writer, "textBackground", value.TextBackground);
        writer.WriteString("textAlign", value.TextAlign.ToString());
        writer.WriteEndObject();
    }

    private static Color? ReadColor(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        return ColorHex.TryParse(reader.GetString(), out var c) ? c : throw new JsonException("Invalid color.");
    }

    private static void WriteColor(Utf8JsonWriter writer, string name, Color? c)
    {
        if (c is { } v) writer.WriteString(name, ColorHex.Argb(v));
        else writer.WriteNull(name);
    }
}

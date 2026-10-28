using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Screeny.Annotations;

/// <summary>Вид стрелки.</summary>
public enum ArrowStyle
{
    /// <summary>Линия со сплошным наконечником на конце.</summary>
    Normal,
    /// <summary>Наконечники с обеих сторон.</summary>
    Double,
    /// <summary>Пунктирная линия со сплошным наконечником.</summary>
    Dashed,
    /// <summary>«Баннерная» стрелка: тело-клин, расширяющееся от начала к широкому наконечнику.</summary>
    Thick,
}

/// <summary>Выравнивание строк многострочного текста внутри его блока (левый верхний угол блока — якорь).</summary>
public enum TextAlign
{
    Left,
    Center,
    Right,
}

/// <summary>
/// Неизменяемый стиль аннотации. Равенство по значению: кэши кистей и перьев в <see cref="Annotation"/>
/// сбрасываются только при реальной смене стиля, а <see cref="AnnotationDocument.Restyle"/> не пишет пустых шагов.
/// <para>
/// <see cref="Color"/> — основной цвет: обводка фигуры, цвет линии, карандаша, текста, метки.
/// Дополнительные поля используют только те фигуры, к которым они относятся; для остальных инструментов
/// они остаются по умолчанию (см. <see cref="Screeny.Overlay.StyleOptions.StyleFor"/>).
/// </para>
/// <para>Сериализуется в JSON (<see cref="AnnotationStyleJsonConverter"/>): цвета — <c>"#AARRGGBB"</c>, стиль стрелки — строкой.</para>
/// </summary>
[JsonConverter(typeof(AnnotationStyleJsonConverter))]
public sealed record AnnotationStyle(Color Color, double Thickness)
{
    /// <summary>Рисовать ли обводку (прямоугольник, эллипс). Выключить можно, только если есть заливка.</summary>
    public bool StrokeEnabled { get; init; } = true;

    /// <summary>Заливка прямоугольника/эллипса вместе с прозрачностью (альфа); null — без заливки.</summary>
    public Color? Fill { get; init; }

    public ArrowStyle Arrow { get; init; } = ArrowStyle.Normal;

    /// <summary>Скруглённая подложка под текстом; null — без подложки.</summary>
    public Color? TextBackground { get; init; }

    /// <summary>Выравнивание строк текста внутри блока; блок при смене выравнивания не сдвигается.</summary>
    public TextAlign TextAlign { get; init; } = TextAlign.Left;

    /// <summary>Цвет обводки или null, если обводка выключена.</summary>
    public Color? StrokeColor => StrokeEnabled ? Color : null;

    /// <summary>Включает/выключает обводку. Выключить последнюю видимую часть фигуры нельзя — стиль не меняется.</summary>
    public AnnotationStyle WithStroke(bool enabled) => !enabled && Fill is null ? this : this with { StrokeEnabled = enabled };

    /// <summary>Задаёт или убирает заливку. Убрать заливку у фигуры без обводки нельзя — стиль не меняется.</summary>
    public AnnotationStyle WithFill(Color? fill) => fill is null && !StrokeEnabled ? this : this with { Fill = fill };

    /// <summary>Можно ли выключить обводку (останется заливка).</summary>
    public bool CanDisableStroke => Fill is not null;

    /// <summary>Можно ли убрать заливку (останется обводка).</summary>
    public bool CanDisableFill => StrokeEnabled;
}

/// <summary>Цвет ↔ текст.</summary>
public static class ColorHex
{
    /// <summary><c>#RRGGBB</c> — как в лупе и в буфер обмена (пипетка). Альфа отбрасывается.</summary>
    public static string Rgb(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary><c>#AARRGGBB</c> — для JSON, где прозрачность важна (заливка 40 %).</summary>
    public static string Argb(Color c) => $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";

    /// <summary>Разбирает <c>#RRGGBB</c>, <c>#AARRGGBB</c> и имена цветов WPF; false — не цвет.</summary>
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

    /// <summary>Тот же цвет с прозрачностью в процентах (0…100).</summary>
    public static Color WithOpacity(Color c, int percent)
        => Color.FromArgb((byte)Math.Round(Math.Clamp(percent, 0, 100) * 255 / 100.0), c.R, c.G, c.B);

    /// <summary>Прозрачность цвета в процентах, округлённая до целого.</summary>
    public static int OpacityPercent(Color c) => (int)Math.Round(c.A * 100 / 255.0);
}

/// <summary>
/// JSON стиля: <c>{ "color": "#FFFF3B30", "thickness": 3, "stroke": true, "fill": "#66FF3B30" | null,
/// "arrow": "Normal", "textBackground": null, "textAlign": "Left" }</c>. Неизвестные поля пропускаются, отсутствующие —
/// по умолчанию (документы без <c>textAlign</c> — выравнивание влево), неверный цвет, стиль стрелки или выравнивание —
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
        // Невидимую фигуру не восстанавливаем: без заливки обводка обязательна.
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

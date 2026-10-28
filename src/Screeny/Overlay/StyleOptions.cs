using System.Windows.Media;
using Screeny.Annotations;
using Screeny.Overlay.Tools;
using Screeny.Settings;

namespace Screeny.Overlay;

/// <summary>
/// Текущие параметры новых фигур, которые не помещаются в палитру и толщину: обводка/заливка, стиль стрелки,
/// подложка текста, активный образец. Без UI — панели параметров и редактор только вызывают методы.
/// <para>
/// Схема правки: панель параметров меняет «действующий» стиль (выбранной фигуры или будущей), результат применяется
/// к выбранной фигуре через <see cref="AnnotationDocument.Restyle"/> и запоминается здесь через <see cref="Absorb"/>.
/// </para>
/// </summary>
public sealed class StyleOptions
{
    /// <summary>Значения, по которым циклически переключается прозрачность заливки (клик по «40 %»).</summary>
    public static readonly int[] FillOpacitySteps = [20, 40, 60, 80, 100];

    /// <summary>Подложка текста по умолчанию — почти чёрная, непрозрачная: любой цвет палитры на ней читается.</summary>
    public static readonly Color DefaultTextBackground = Color.FromRgb(0x1C, 0x19, 0x17);

    public bool StrokeEnabled { get; private set; } = true;
    public bool FillEnabled { get; private set; }
    /// <summary>Цвет заливки без прозрачности; прозрачность — <see cref="FillOpacity"/>.</summary>
    public Color FillColor { get; private set; } = Colors.Red;
    public int FillOpacity { get; private set; } = 40;
    public ArrowStyle Arrow { get; private set; } = ArrowStyle.Normal;
    public bool TextBackgroundEnabled { get; private set; }
    public Color TextBackgroundColor { get; private set; } = DefaultTextBackground;
    public TextAlign TextAlign { get; private set; } = TextAlign.Left;
    /// <summary>Подсвеченный образец параметров: его красит палитра.</summary>
    public PaintTarget ActiveTarget { get; set; } = PaintTarget.Color;

    /// <summary>Цвет заливки по умолчанию — цвет палитры (с прозрачностью из настроек).</summary>
    public static StyleOptions FromSettings(AppSettings s, Color paletteColor)
    {
        var o = new StyleOptions
        {
            StrokeEnabled = s.LastStrokeEnabled,
            FillEnabled = s.LastFillEnabled,
            FillColor = Opaque(paletteColor),
            FillOpacity = Math.Clamp(s.LastFillOpacity, AppSettings.MinFillOpacity, AppSettings.MaxFillOpacity),
            Arrow = Enum.IsDefined(s.LastArrowStyle) ? s.LastArrowStyle : ArrowStyle.Normal,
            TextBackgroundEnabled = s.LastTextBackground,
            TextAlign = Enum.IsDefined(s.LastTextAlign) ? s.LastTextAlign : TextAlign.Left,
        };
        if (!o.StrokeEnabled && !o.FillEnabled) o.StrokeEnabled = true;
        return o;
    }

    public void SaveTo(AppSettings s)
    {
        s.LastStrokeEnabled = StrokeEnabled;
        s.LastFillEnabled = FillEnabled;
        s.LastFillOpacity = FillOpacity;
        s.LastArrowStyle = Arrow;
        s.LastTextBackground = TextBackgroundEnabled;
        s.LastTextAlign = TextAlign;
    }

    /// <summary>
    /// Стиль новой фигуры инструмента. Заливка — только у прямоугольника и эллипса, стиль стрелки — только у стрелки,
    /// подложка — только у текста; остальные инструменты получают стиль из одного цвета и толщины.
    /// </summary>
    public AnnotationStyle StyleFor(ToolKind tool, Color color, double thickness)
    {
        var style = new AnnotationStyle(color, thickness);
        return tool switch
        {
            ToolKind.Rect or ToolKind.Ellipse => style with
            {
                StrokeEnabled = StrokeEnabled || !FillEnabled,
                Fill = FillEnabled ? ColorHex.WithOpacity(FillColor, FillOpacity) : null,
            },
            ToolKind.Arrow => style with { Arrow = Arrow },
            ToolKind.Text => style with { TextBackground = TextBackgroundEnabled ? TextBackgroundColor : null, TextAlign = TextAlign },
            _ => style,
        };
    }

    /// <summary>Запоминает параметры секции из стиля, который пользователь только что изменил.</summary>
    public void Absorb(AnnotationStyle style, OptionsSectionKind section)
    {
        switch (section)
        {
            case OptionsSectionKind.Shape:
                StrokeEnabled = style.StrokeEnabled || style.Fill is null;
                FillEnabled = style.Fill is not null;
                if (style.Fill is { } fill) { FillColor = Opaque(fill); FillOpacity = ClampOpacity(ColorHex.OpacityPercent(fill)); }
                break;
            case OptionsSectionKind.Arrow:
                Arrow = style.Arrow;
                break;
            case OptionsSectionKind.Text:
                TextBackgroundEnabled = style.TextBackground is not null;
                if (style.TextBackground is { } bg) TextBackgroundColor = bg;
                TextAlign = style.TextAlign;
                break;
        }
    }

    // ---------- Операции параметров над стилем (чистые: возвращают новый стиль) ----------

    /// <summary>Кнопка ⊘ у обводки: переключает обводку (последнюю видимую часть не выключает).</summary>
    public AnnotationStyle ToggleStroke(AnnotationStyle s) => s.WithStroke(!s.StrokeEnabled);

    /// <summary>Кнопка ⊘ у заливки: убирает заливку или возвращает её запомненным цветом и прозрачностью.</summary>
    public AnnotationStyle ToggleFill(AnnotationStyle s)
        => s.WithFill(s.Fill is null ? ColorHex.WithOpacity(FillColor, FillOpacity) : null);

    /// <summary>Клик по «40 %»: следующая прозрачность заливки (заливка при этом включается).</summary>
    public AnnotationStyle NextFillOpacity(AnnotationStyle s)
    {
        int current = s.Fill is { } f ? ColorHex.OpacityPercent(f) : FillOpacity;
        int next = FillOpacitySteps.FirstOrDefault(step => step > current + 1, FillOpacitySteps[0]);
        var rgb = s.Fill is { } fill ? Opaque(fill) : FillColor;
        FillOpacity = next; // и без заливки следующая включённая получит эту прозрачность
        return s.WithFill(ColorHex.WithOpacity(rgb, next));
    }

    public AnnotationStyle ToggleTextBackground(AnnotationStyle s)
        => s with { TextBackground = s.TextBackground is null ? TextBackgroundColor : null };

    public static AnnotationStyle WithArrow(AnnotationStyle s, ArrowStyle arrow) => s with { Arrow = arrow };

    public static AnnotationStyle WithTextAlign(AnnotationStyle s, TextAlign align) => s with { TextAlign = align };

    /// <summary>
    /// Цвет из палитры или пипетки в образец <paramref name="target"/>. Покраска выключенной обводки, заливки
    /// или подложки включает её. Прозрачность заливки сохраняется.
    /// </summary>
    public AnnotationStyle Paint(AnnotationStyle s, PaintTarget target, Color c) => target switch
    {
        PaintTarget.Fill => s.WithFill(ColorHex.WithOpacity(c, s.Fill is { } f ? ColorHex.OpacityPercent(f) : FillOpacity)),
        PaintTarget.Background => s with { TextBackground = Opaque(c) },
        _ => s with { Color = Opaque(c), StrokeEnabled = true },
    };

    /// <summary>Цвет, который должен быть подсвечен на палитре для образца <paramref name="target"/>.</summary>
    public static Color? SwatchColor(AnnotationStyle s, PaintTarget target) => target switch
    {
        PaintTarget.Fill => s.Fill is { } f ? Opaque(f) : null,
        PaintTarget.Background => s.TextBackground,
        _ => s.Color,
    };

    private static int ClampOpacity(int v) => Math.Clamp(v, AppSettings.MinFillOpacity, AppSettings.MaxFillOpacity);
    public static Color Opaque(Color c) => Color.FromRgb(c.R, c.G, c.B);
}

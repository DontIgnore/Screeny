using System.Windows.Media;
using Screeny.Annotations;
using Screeny.Overlay.Tools;
using Screeny.Settings;

namespace Screeny.Overlay;

/// <summary>
/// Current parameters of new shapes not fitting palette and thickness: stroke/fill, arrow style,
/// text background, active swatch. Without UI — options panel and editor only call methods.
/// <para>
/// Edit scheme: options panel changes “active” style (selected shape or future), result applied
/// to selected shape via <see cref="AnnotationDocument.Restyle"/> and remembered here via <see cref="Absorb"/>.
/// </para>
/// </summary>
public sealed class StyleOptions
{
    /// <summary>Values fill opacity cycles through (click on “40%”).</summary>
    public static readonly int[] FillOpacitySteps = [20, 40, 60, 80, 100];

    /// <summary>Default text background — almost black opaque: any palette color readable on it.</summary>
    public static readonly Color DefaultTextBackground = Color.FromRgb(0x1C, 0x19, 0x17);

    public bool StrokeEnabled { get; private set; } = true;
    public bool FillEnabled { get; private set; }
    /// <summary>Fill color without opacity; opacity — <see cref="FillOpacity"/>.</summary>
    public Color FillColor { get; private set; } = Colors.Red;
    public int FillOpacity { get; private set; } = 40;
    public ArrowStyle Arrow { get; private set; } = ArrowStyle.Normal;
    public bool TextBackgroundEnabled { get; private set; }
    public Color TextBackgroundColor { get; private set; } = DefaultTextBackground;
    public TextAlign TextAlign { get; private set; } = TextAlign.Left;
    /// <summary>Highlighted options swatch: palette paints it.</summary>
    public PaintTarget ActiveTarget { get; set; } = PaintTarget.Color;

    /// <summary>Default fill color — palette color (with opacity from settings).</summary>
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
    /// Style of future shape of tool. Fill only for rectangle/ellipse, arrow style only for arrow,
    /// background only for text; other tools get style from one color and thickness.
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

    /// <summary>Remembers section parameters from style user just changed.</summary>
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

    // ---------- Style operations (pure: return new style) ----------

    /// <summary>⊘ button at stroke: toggles stroke (doesn't disable last visible part).</summary>
    public AnnotationStyle ToggleStroke(AnnotationStyle s) => s.WithStroke(!s.StrokeEnabled);

    /// <summary>⊘ button at fill: removes fill or restores with remembered color and opacity.</summary>
    public AnnotationStyle ToggleFill(AnnotationStyle s)
        => s.WithFill(s.Fill is null ? ColorHex.WithOpacity(FillColor, FillOpacity) : null);

    /// <summary>Click on “40%”: next fill opacity (fill enabled).</summary>
    public AnnotationStyle NextFillOpacity(AnnotationStyle s)
    {
        int current = s.Fill is { } f ? ColorHex.OpacityPercent(f) : FillOpacity;
        int next = FillOpacitySteps.FirstOrDefault(step => step > current + 1, FillOpacitySteps[0]);
        var rgb = s.Fill is { } fill ? Opaque(fill) : FillColor;
        FillOpacity = next; // and without fill, next enabled fill gets this opacity
        return s.WithFill(ColorHex.WithOpacity(rgb, next));
    }

    public AnnotationStyle ToggleTextBackground(AnnotationStyle s)
        => s with { TextBackground = s.TextBackground is null ? TextBackgroundColor : null };

    public static AnnotationStyle WithArrow(AnnotationStyle s, ArrowStyle arrow) => s with { Arrow = arrow };

    public static AnnotationStyle WithTextAlign(AnnotationStyle s, TextAlign align) => s with { TextAlign = align };

    /// <summary>
    /// Color from palette or eyedropper into swatch <paramref name="target"/>. Painting disabled stroke, fill
    /// or background enables it. Fill opacity preserved.
    /// </summary>
    public AnnotationStyle Paint(AnnotationStyle s, PaintTarget target, Color c) => target switch
    {
        PaintTarget.Fill => s.WithFill(ColorHex.WithOpacity(c, s.Fill is { } f ? ColorHex.OpacityPercent(f) : FillOpacity)),
        PaintTarget.Background => s with { TextBackground = Opaque(c) },
        _ => s with { Color = Opaque(c), StrokeEnabled = true },
    };

    /// <summary>Color to highlight on palette for swatch <paramref name="target"/>.</summary>
    public static Color? SwatchColor(AnnotationStyle s, PaintTarget target) => target switch
    {
        PaintTarget.Fill => s.Fill is { } f ? Opaque(f) : null,
        PaintTarget.Background => s.TextBackground,
        _ => s.Color,
    };

    private static int ClampOpacity(int v) => Math.Clamp(v, AppSettings.MinFillOpacity, AppSettings.MaxFillOpacity);
    public static Color Opaque(Color c) => Color.FromRgb(c.R, c.G, c.B);
}

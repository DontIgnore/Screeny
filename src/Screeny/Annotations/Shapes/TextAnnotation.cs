using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Screeny.Annotations.Shapes;

/// <summary>
/// Text; with <see cref="AnnotationStyle.TextBackground"/> — on rounded background. Multiline text lines
/// are aligned (<see cref="AnnotationStyle.TextAlign"/>) inside block of width of longest line; top-left
/// corner of block is <see cref="Origin"/>, so alignment change does not move or resize block.
/// </summary>
public sealed class TextAnnotation(Point origin, string text, AnnotationStyle style) : Annotation(style)
{
    /// <summary>Background padding from text horizontally and vertically, screenshot pixels.</summary>
    public const double BackgroundPadX = 8, BackgroundPadY = 4;
    public const double BackgroundRadius = 6;

    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush ShadowBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)));

    private string _text = text;
    /// <summary>
    /// Extra width for center/right alignment: MaxTextWidth exactly measured width due to rounding
    /// may wrap longest line. Text is drawn left by this extra (half for center).
    /// </summary>
    private const double AlignSlack = 1;

    private FormattedText? _formatted; // one per annotation; reset on text or style change
    private double _blockWidth;
    private SolidColorBrush? _background;

    public Point Origin { get; private set; } = origin;

    public string Text
    {
        get => _text;
        set { if (_text == value) return; _text = value; _formatted = null; }
    }

    /// <summary>Font size in screenshot pixels — same as text input field in overlay.</summary>
    public static double FontSizeFor(double thickness) => Math.Max(14, thickness * 6);

    private FormattedText Formatted
    {
        get
        {
            if (_formatted is not null) return _formatted;
            var ft = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face,
                FontSizeFor(Style.Thickness), StyleBrush, pixelsPerDip: 1.0);
            // Block width by longest line without limit: same at any alignment.
            _blockWidth = Math.Max(1, ft.WidthIncludingTrailingWhitespace);
            if (Style.TextAlign != TextAlign.Left)
            {
                ft.MaxTextWidth = _blockWidth + AlignSlack;
                ft.TextAlignment = Style.TextAlign == TextAlign.Center ? TextAlignment.Center : TextAlignment.Right;
            }
            return _formatted = ft;
        }
    }

    /// <summary>Where to draw FormattedText so block starts exactly at <see cref="Origin"/> (accounting for width reserve).</summary>
    private Point DrawOrigin => Style.TextAlign switch
    {
        TextAlign.Center => Origin - new Vector(AlignSlack / 2, 0),
        TextAlign.Right => Origin - new Vector(AlignSlack, 0),
        _ => Origin,
    };

    protected override void OnStyleChanged() { _formatted = null; _background = null; }

    /// <summary>Rectangle of text itself (without background).</summary>
    public Rect TextBounds
    {
        get { var ft = Formatted; return new Rect(Origin, new Size(_blockWidth, ft.Height)); }
    }

    /// <summary>Background around text with padding <see cref="BackgroundPadX"/>×<see cref="BackgroundPadY"/>.</summary>
    public static Rect BackgroundRect(Rect text) => Rect.Inflate(text, BackgroundPadX, BackgroundPadY);

    /// <summary>Text itself does not move: background expands bounds outward.</summary>
    public override Rect Bounds => Style.TextBackground is null ? TextBounds : BackgroundRect(TextBounds);

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var ft = Formatted;
        if (Style.TextBackground is { } bg)
        {
            // No shadow on background: readability comes from background.
            dc.DrawRoundedRectangle(_background ??= Freeze(new SolidColorBrush(bg)), null, BackgroundRect(TextBounds), BackgroundRadius, BackgroundRadius);
            ft.SetForegroundBrush(StyleBrush);
            dc.DrawText(ft, DrawOrigin);
            return;
        }
        // Thin 1 px shadow for readability on any background. DrawText turns text into glyphs immediately
        // with current brush, so one FormattedText can be drawn twice with different colors.
        ft.SetForegroundBrush(ShadowBrush);
        dc.DrawText(ft, DrawOrigin + new Vector(1, 1));
        ft.SetForegroundBrush(StyleBrush);
        dc.DrawText(ft, DrawOrigin);
    }

    public override void Offset(Vector delta) => Origin += delta;
}

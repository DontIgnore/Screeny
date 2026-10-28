using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Screeny.Annotations.Shapes;

/// <summary>
/// Текст; с <see cref="AnnotationStyle.TextBackground"/> — на скруглённой подложке. Строки многострочного текста
/// выравниваются (<see cref="AnnotationStyle.TextAlign"/>) внутри блока шириной в самую длинную строку; левый верхний
/// угол блока — <see cref="Origin"/>, поэтому смена выравнивания блок не сдвигает и не меняет его размер.
/// </summary>
public sealed class TextAnnotation(Point origin, string text, AnnotationStyle style) : Annotation(style)
{
    /// <summary>Отступ подложки от текста по горизонтали и вертикали, пиксели снимка.</summary>
    public const double BackgroundPadX = 8, BackgroundPadY = 4;
    public const double BackgroundRadius = 6;

    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Brush ShadowBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)));

    private string _text = text;
    /// <summary>
    /// Запас ширины для выравнивания по центру и вправо: MaxTextWidth ровно по измеренной ширине из-за округления
    /// может перенести самую длинную строку. Текст рисуется левее на этот запас (на половину — по центру).
    /// </summary>
    private const double AlignSlack = 1;

    private FormattedText? _formatted; // один на аннотацию; сбрасывается при смене текста или стиля
    private double _blockWidth;
    private SolidColorBrush? _background;

    public Point Origin { get; private set; } = origin;

    public string Text
    {
        get => _text;
        set { if (_text == value) return; _text = value; _formatted = null; }
    }

    /// <summary>Размер шрифта в пикселях снимка — тот же, что у поля ввода текста в оверлее.</summary>
    public static double FontSizeFor(double thickness) => Math.Max(14, thickness * 6);

    private FormattedText Formatted
    {
        get
        {
            if (_formatted is not null) return _formatted;
            var ft = new FormattedText(Text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face,
                FontSizeFor(Style.Thickness), StyleBrush, pixelsPerDip: 1.0);
            // Ширина блока — по самой длинной строке без ограничения: одинакова при любом выравнивании.
            _blockWidth = Math.Max(1, ft.WidthIncludingTrailingWhitespace);
            if (Style.TextAlign != TextAlign.Left)
            {
                ft.MaxTextWidth = _blockWidth + AlignSlack;
                ft.TextAlignment = Style.TextAlign == TextAlign.Center ? TextAlignment.Center : TextAlignment.Right;
            }
            return _formatted = ft;
        }
    }

    /// <summary>Где рисовать FormattedText, чтобы блок начинался ровно в <see cref="Origin"/> (с учётом запаса ширины).</summary>
    private Point DrawOrigin => Style.TextAlign switch
    {
        TextAlign.Center => Origin - new Vector(AlignSlack / 2, 0),
        TextAlign.Right => Origin - new Vector(AlignSlack, 0),
        _ => Origin,
    };

    protected override void OnStyleChanged() { _formatted = null; _background = null; }

    /// <summary>Прямоугольник самого текста (без подложки).</summary>
    public Rect TextBounds
    {
        get { var ft = Formatted; return new Rect(Origin, new Size(_blockWidth, ft.Height)); }
    }

    /// <summary>Подложка вокруг текста с отступом <see cref="BackgroundPadX"/>×<see cref="BackgroundPadY"/>.</summary>
    public static Rect BackgroundRect(Rect text) => Rect.Inflate(text, BackgroundPadX, BackgroundPadY);

    /// <summary>Текст сам по себе не сдвигается: подложка расширяет bounds наружу.</summary>
    public override Rect Bounds => Style.TextBackground is null ? TextBounds : BackgroundRect(TextBounds);

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var ft = Formatted;
        if (Style.TextBackground is { } bg)
        {
            // На подложке тень не нужна: читаемость даёт фон.
            dc.DrawRoundedRectangle(_background ??= Freeze(new SolidColorBrush(bg)), null, BackgroundRect(TextBounds), BackgroundRadius, BackgroundRadius);
            ft.SetForegroundBrush(StyleBrush);
            dc.DrawText(ft, DrawOrigin);
            return;
        }
        // Тонкая тень на 1 px для читаемости на любом фоне. DrawText сразу превращает текст в глифы
        // с текущей кистью, поэтому один FormattedText можно нарисовать дважды разными цветами.
        ft.SetForegroundBrush(ShadowBrush);
        dc.DrawText(ft, DrawOrigin + new Vector(1, 1));
        ft.SetForegroundBrush(StyleBrush);
        dc.DrawText(ft, DrawOrigin);
    }

    public override void Offset(Vector delta) => Origin += delta;
}

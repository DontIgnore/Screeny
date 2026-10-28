using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Screeny.Annotations.Shapes;

/// <summary>Нумерованная метка: залитый круг цвета стиля с белой цифрой.</summary>
public sealed class NumberAnnotation(Point center, int number, AnnotationStyle style) : Annotation(style)
{
    private static readonly Typeface Face = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Brush OutlineBrush = Freeze(new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)));

    private Pen? _outline;            // зависят от радиуса, т.е. от стиля
    private FormattedText? _formatted;

    public Point Center { get; private set; } = center;
    public int Number { get; } = number;

    private double Radius => Math.Max(11, Style.Thickness * 4);

    private double OutlineWidth => Math.Max(1.5, Radius / 8);

    /// <summary>Круг плюс половина белой обводки по его краю.</summary>
    public override Rect Bounds => GeometryBounds.InflateCopy(OutlineWidth / 2);
    public override Rect GeometryBounds => new(Center.X - Radius, Center.Y - Radius, Radius * 2, Radius * 2);

    public override bool HitTest(Point p) => (p - Center).Length <= Radius + 2;

    protected override void OnStyleChanged() { _outline = null; _formatted = null; }

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        _outline ??= Freeze(new Pen(OutlineBrush, OutlineWidth));
        dc.DrawEllipse(StyleBrush, _outline, Center, Radius, Radius);
        var ft = _formatted ??= new FormattedText(Number.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Face, Radius * (Number >= 10 ? 0.95 : 1.15), Brushes.White, pixelsPerDip: 1.0);
        dc.DrawText(ft, new Point(Center.X - ft.Width / 2, Center.Y - ft.Height / 2));
    }

    public override void Offset(Vector delta) => Center += delta;
}

using System.Windows;
using System.Windows.Media;

namespace Screeny.Annotations.Shapes;

/// <summary>Стрелка одного из стилей <see cref="ArrowStyle"/>; геометрию считает <see cref="ArrowGeometry"/>.</summary>
public sealed class ArrowAnnotation(Point start, Point end, AnnotationStyle style) : LineAnnotation(start, end, style)
{
    // Кэши сбрасываются при изменении концов или стиля.
    private ArrowShape? _shape;
    private StreamGeometry? _fill;
    private Pen? _edgePen, _dashPen;

    /// <summary>Текущая геометрия (для отрисовки и тестов).</summary>
    public ArrowShape Shape => _shape ??= ArrowGeometry.Compute(Start, End, Style.Thickness, Style.Arrow);

    public override Rect Bounds => Shape.Polygons.Count == 0 ? base.Bounds : ArrowGeometry.Bounds(Shape, Style.Thickness);
    public override Rect GeometryBounds => Bounds;

    /// <summary>Совместимость: крылья треугольного наконечника на конце <paramref name="to"/>.</summary>
    public static (Point Left, Point Right) HeadWings(Point from, Point to, double headLength, double headWidth)
    {
        var dir = to - from; if (dir.Length < 1e-6) return (to, to);
        dir.Normalize();
        var basePt = to - dir * headLength;
        var normal = new Vector(-dir.Y, dir.X) * (headWidth / 2);
        return (basePt + normal * -1, basePt + normal);
    }

    protected override void OnGeometryChanged() { _shape = null; _fill = null; }
    protected override void OnStyleChanged() { _shape = null; _fill = null; _edgePen = null; _dashPen = null; }

    public override bool HitTest(Point p) => ArrowGeometry.HitTest(Shape, p, Style.Thickness / 2 + 4);

    public override void Render(DrawingContext dc, RenderContext ctx)
    {
        var shape = Shape;
        if (shape.Polygons.Count == 0) return;
        if (shape.HasLine) dc.DrawLine(shape.Dashed ? DashPen() : MakePen(), shape.LineStart, shape.LineEnd);
        // Тонкое перо по краю скругляет углы наконечников.
        dc.DrawGeometry(StyleBrush, _edgePen ??= Freeze(new Pen(StyleBrush, 1) { LineJoin = PenLineJoin.Round }), FillGeometry(shape));
    }

    private Pen DashPen() => _dashPen ??= Freeze(new Pen(StyleBrush, Style.Thickness)
    {
        // Штрих 3 толщины, промежуток 2: круглые концы съедают часть промежутка.
        DashStyle = new DashStyle([3, 2.2], 0), DashCap = PenLineCap.Round,
        StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round,
    });

    private StreamGeometry FillGeometry(ArrowShape shape)
    {
        if (_fill is not null) return _fill;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            foreach (var poly in shape.Polygons)
            {
                ctx.BeginFigure(poly[0], isFilled: true, isClosed: true);
                for (int i = 1; i < poly.Length; i++) ctx.LineTo(poly[i], true, false);
            }
        }
        g.Freeze();
        return _fill = g;
    }
}

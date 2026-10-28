using System.Windows;
using System.Windows.Media;

namespace Screeny.Annotations.Shapes;

public class LineAnnotation(Point start, Point end, AnnotationStyle style) : Annotation(style)
{
    private Point _start = start, _end = end;

    public Point Start { get => _start; set { if (_start == value) return; _start = value; OnGeometryChanged(); } }
    public Point End { get => _end; set { if (_end == value) return; _end = value; OnGeometryChanged(); } }

    /// <summary>Концы линии плюс половина толщины пера (круглые концы и края штриха).</summary>
    public override Rect Bounds => new Rect(Start, End).InflateCopy(Style.Thickness / 2);
    public override Rect GeometryBounds => new(Start, End);
    public override void Render(DrawingContext dc, RenderContext ctx) => dc.DrawLine(MakePen(), Start, End);

    public override bool HitTest(Point p)
    {
        var hit = HitPen();
        return new Rect(Start, End).InflateCopy(hit.Thickness / 2).Contains(p)
            && new LineGeometry(Start, End).StrokeContains(hit, p);
    }

    public override void Offset(Vector delta) { _start += delta; _end += delta; OnGeometryChanged(); }

    /// <summary>Вызывается после изменения Start/End.</summary>
    protected virtual void OnGeometryChanged() { }
}

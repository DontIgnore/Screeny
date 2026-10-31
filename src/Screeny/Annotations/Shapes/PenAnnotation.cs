using System.Windows;
using System.Windows.Media;

namespace Screeny.Annotations.Shapes;

/// <summary>
/// Freehand pencil line. Geometry grows on AddPoint (PolyLineSegment.Points.Add),
/// not rebuilt from all points; StreamGeometry is unsuitable — Open() clears content.
/// </summary>
public class PenAnnotation : Annotation
{
    private readonly List<Point> _points;
    private readonly PathFigure _figure;
    private readonly PolyLineSegment _segment;
    private readonly PathGeometry _geometry;
    private Rect _pointBounds;      // bounds of points themselves, without pen thickness
    private Pen? _strokePen, _hitPen;

    public PenAnnotation(Point start, AnnotationStyle style) : base(style)
    {
        _points = [start];
        _pointBounds = new Rect(start, start);
        // Single point — draw micro-segment so pencil click leaves a dot.
        _segment = new PolyLineSegment([start + new Vector(0.01, 0)], isStroked: true) { IsSmoothJoin = true };
        _figure = new PathFigure { StartPoint = start, IsFilled = false, IsClosed = false };
        _figure.Segments.Add(_segment);
        _geometry = new PathGeometry([_figure]);
    }

    public IReadOnlyList<Point> Points => _points;

    public void AddPoint(Point p)
    {
        if (_points.Count == 1) _segment.Points[0] = p; // micro-segment no longer needed
        else _segment.Points.Add(p);
        _points.Add(p);
        _pointBounds.Union(p);
    }

    /// <summary>Stroke pen; created once until style change.</summary>
    protected virtual Pen CreateStrokePen() => MakePen();

    protected Pen StrokePen => _strokePen ??= CreateStrokePen();
    private Pen StrokeHitPen => _hitPen ??= Freeze(new Pen(Brushes.Black, StrokePen.Thickness + 8));

    protected override void OnStyleChanged() { _strokePen = null; _hitPen = null; }

    protected Geometry Geometry => _geometry;

    /// <summary>O(1): bounds of points plus half thickness (for square caps — diagonal).</summary>
    public override Rect Bounds
    {
        get
        {
            var pen = StrokePen;
            double pad = pen.Thickness / 2 * (pen.StartLineCap == PenLineCap.Square || pen.EndLineCap == PenLineCap.Square ? Math.Sqrt(2) : 1);
            return Rect.Inflate(_pointBounds, pad, pad);
        }
    }

    public override void Render(DrawingContext dc, RenderContext ctx) => dc.DrawGeometry(null, StrokePen, _geometry);

    public override bool HitTest(Point p)
    {
        var hit = StrokeHitPen;
        // Cheap bounds prefilter, then exact geometry check.
        return Rect.Inflate(_pointBounds, hit.Thickness / 2, hit.Thickness / 2).Contains(p) && _geometry.StrokeContains(hit, p);
    }

    public override void Offset(Vector delta)
    {
        for (int i = 0; i < _points.Count; i++) _points[i] += delta;
        _figure.StartPoint += delta;
        var moved = new PointCollection(_segment.Points.Count);
        foreach (var pt in _segment.Points) moved.Add(pt + delta);
        _segment.Points = moved;
        _pointBounds.Offset(delta);
    }
}

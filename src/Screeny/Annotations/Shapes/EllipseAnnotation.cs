using System.Windows;
using System.Windows.Media;

namespace Screeny.Annotations.Shapes;

/// <summary>Ellipse: stroke and/or fill (<see cref="AnnotationStyle.Fill"/>).</summary>
public sealed class EllipseAnnotation(Rect rect, AnnotationStyle style) : BoxAnnotation(rect, style)
{
    private EllipseGeometry? _geometry;

    private EllipseGeometry Geometry => _geometry ??= Freeze(new EllipseGeometry(Rect));

    protected override void OnGeometryChanged() => _geometry = null;

    /// <summary>Stroke lies on outline: half its thickness outside ellipse.</summary>
    public override Rect Bounds => Style.StrokeEnabled ? Rect.InflateCopy(Style.Thickness / 2) : Rect;

    public override void Render(DrawingContext dc, RenderContext ctx) => dc.DrawGeometry(FillBrush, StrokePenOrNull, Geometry);

    /// <summary>Without fill — outline only; filled ellipse hit-tests across whole area (and outline).</summary>
    public override bool HitTest(Point p)
    {
        if (Rect.IsEmpty) return false;
        var hit = HitPen();
        if (!Rect.InflateCopy(hit.Thickness / 2).Contains(p)) return false;
        return (Style.Fill is not null && Geometry.FillContains(p)) || Geometry.StrokeContains(hit, p);
    }
}

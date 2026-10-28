using System.Windows;
using System.Windows.Media;

namespace Screeny.Annotations.Shapes;

/// <summary>Эллипс: обводка и/или заливка (<see cref="AnnotationStyle.Fill"/>).</summary>
public sealed class EllipseAnnotation(Rect rect, AnnotationStyle style) : BoxAnnotation(rect, style)
{
    private EllipseGeometry? _geometry;

    private EllipseGeometry Geometry => _geometry ??= Freeze(new EllipseGeometry(Rect));

    protected override void OnGeometryChanged() => _geometry = null;

    /// <summary>Обводка лежит по контуру: половина её толщины снаружи эллипса.</summary>
    public override Rect Bounds => Style.StrokeEnabled ? Rect.InflateCopy(Style.Thickness / 2) : Rect;

    public override void Render(DrawingContext dc, RenderContext ctx) => dc.DrawGeometry(FillBrush, StrokePenOrNull, Geometry);

    /// <summary>Без заливки — только по контуру; залитый эллипс ловится по всей площади (и по контуру).</summary>
    public override bool HitTest(Point p)
    {
        if (Rect.IsEmpty) return false;
        var hit = HitPen();
        if (!Rect.InflateCopy(hit.Thickness / 2).Contains(p)) return false;
        return (Style.Fill is not null && Geometry.FillContains(p)) || Geometry.StrokeContains(hit, p);
    }
}

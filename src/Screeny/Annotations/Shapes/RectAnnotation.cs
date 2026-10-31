using System.Windows;
using System.Windows.Media;

namespace Screeny.Annotations.Shapes;

/// <summary>Rectangle: stroke and/or fill (<see cref="AnnotationStyle.Fill"/>).</summary>
public sealed class RectAnnotation(Rect rect, AnnotationStyle style) : BoxAnnotation(rect, style)
{
    /// <summary>Stroke lies on outline: half its thickness outside rectangle.</summary>
    public override Rect Bounds => Style.StrokeEnabled ? Rect.InflateCopy(Style.Thickness / 2) : Rect;

    public override void Render(DrawingContext dc, RenderContext ctx) => dc.DrawRectangle(FillBrush, StrokePenOrNull, Rect);

    /// <summary>
    /// Without fill — only border band: click inside passes to lower annotations or starts new shape.
    /// Small shape has no inner “transparent” area — entire bounds hit-tests.
    /// Filled shape hit-tests across whole area.
    /// </summary>
    public override bool HitTest(Point p)
    {
        double hitMargin = (Style.StrokeEnabled ? Style.Thickness / 2 : 0) + 4;
        if (!Rect.InflateCopy(hitMargin).Contains(p)) return false;
        if (Style.Fill is not null) return true;
        var interior = Rect.InflateCopy(-hitMargin);
        return interior.IsEmpty || !interior.Contains(p);
    }
}

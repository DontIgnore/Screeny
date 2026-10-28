using System.Windows;
using System.Windows.Media;

namespace Screeny.Annotations.Shapes;

/// <summary>Прямоугольник: обводка и/или заливка (<see cref="AnnotationStyle.Fill"/>).</summary>
public sealed class RectAnnotation(Rect rect, AnnotationStyle style) : BoxAnnotation(rect, style)
{
    /// <summary>Обводка лежит по контуру: половина её толщины снаружи прямоугольника.</summary>
    public override Rect Bounds => Style.StrokeEnabled ? Rect.InflateCopy(Style.Thickness / 2) : Rect;

    public override void Render(DrawingContext dc, RenderContext ctx) => dc.DrawRectangle(FillBrush, StrokePenOrNull, Rect);

    /// <summary>
    /// Без заливки — только полоса рамки: клик внутри проходит к нижним аннотациям или начинает новую фигуру.
    /// У маленькой фигуры внутренней «прозрачной» области нет — попаданием считается весь bounds.
    /// Залитая фигура ловится по всей площади.
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

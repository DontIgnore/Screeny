using System.Windows;
using System.Windows.Media;

namespace Screeny.Annotations.Shapes;

/// <summary>Маркер: полупрозрачная широкая линия с квадратными концами.</summary>
public sealed class HighlighterAnnotation(Point start, AnnotationStyle style) : PenAnnotation(start, style)
{
    protected override Pen CreateStrokePen()
    {
        var c = Style.Color;
        return Freeze(new Pen(new SolidColorBrush(Color.FromArgb(0x66, c.R, c.G, c.B)), Style.Thickness * 4)
        { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square, LineJoin = PenLineJoin.Round });
    }
}

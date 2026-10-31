using System.Windows;

namespace Screeny.Annotations.Shapes;

/// <summary>
/// Arrow geometry without WPF rendering: line (if any) and filled polygons (arrowheads or wedge body).
/// </summary>
/// <param name="HasLine">Whether line <paramref name="LineStart"/>→<paramref name="LineEnd"/> is drawn with style pen.</param>
/// <param name="Dashed">Line is dashed.</param>
/// <param name="Polygons">Filled polygons: arrowheads (first point is tip) or body of “thick” arrow.</param>
public sealed record ArrowShape(bool HasLine, Point LineStart, Point LineEnd, bool Dashed, IReadOnlyList<Point[]> Polygons)
{
    public static readonly ArrowShape Empty = new(false, default, default, false, []);
}

public static class ArrowGeometry
{
    /// <summary>Arrowhead length for normal arrow at given thickness (shorter for short arrow).</summary>
    public static double HeadLength(double thickness) => Math.Max(12, thickness * 4);

    public static ArrowShape Compute(Point start, Point end, double thickness, ArrowStyle style)
    {
        var dir = end - start;
        double length = dir.Length;
        if (length < 1e-6) return ArrowShape.Empty;
        dir /= length;
        return style switch
        {
            ArrowStyle.Double => Double(start, end, dir, length, thickness),
            ArrowStyle.Thick => Thick(start, end, dir, length, thickness),
            _ => Single(start, end, dir, length, thickness, dashed: style == ArrowStyle.Dashed),
        };
    }

    /// <summary>Normal and dashed: line ends at arrowhead base so it doesn't stick out of tip.</summary>
    private static ArrowShape Single(Point start, Point end, Vector dir, double length, double thickness, bool dashed)
    {
        // Short arrow — reduce arrowhead so it doesn't “invert” past the line start.
        double head = Math.Min(HeadLength(thickness), length * 0.6);
        return new ArrowShape(true, start, end - dir * (head * 0.9), dashed, [Head(end, dir, head)]);
    }

    /// <summary>Double-ended: arrowheads on both ends, each no longer than 40% of arrow.</summary>
    private static ArrowShape Double(Point start, Point end, Vector dir, double length, double thickness)
    {
        double head = Math.Min(HeadLength(thickness), length * 0.4);
        return new ArrowShape(true, start + dir * (head * 0.9), end - dir * (head * 0.9), false,
            [Head(end, dir, head), Head(start, -dir, head)]);
    }

    /// <summary>
    /// “Banner” arrow as one polygon: narrow tail at start, body widens to neck,
    /// wide arrowhead. Point order: tail, neck, wing, tip, wing, neck, tail.
    /// </summary>
    private static ArrowShape Thick(Point start, Point end, Vector dir, double length, double thickness)
    {
        double head = Math.Min(Math.Max(16, thickness * 5), length * 0.5);
        double body = Math.Max(3, thickness * 1.2), wing = Math.Max(body * 2.2, head * 0.55), tail = Math.Max(0.5, thickness * 0.2);
        var n = new Vector(-dir.Y, dir.X);
        var neck = end - dir * head;
        Point[] polygon =
        [
            start + n * tail, neck + n * body, neck + n * wing, end,
            neck - n * wing, neck - n * body, start - n * tail,
        ];
        return new ArrowShape(false, start, end, false, [polygon]);
    }

    /// <summary>Arrowhead triangle: tip, left and right wing.</summary>
    private static Point[] Head(Point tip, Vector dir, double head)
    {
        var basePt = tip - dir * head;
        var normal = new Vector(-dir.Y, dir.X) * (head * 0.8 / 2);
        return [tip, basePt - normal, basePt + normal];
    }

    /// <summary>Bounds of all shape points expanded by half line thickness.</summary>
    public static Rect Bounds(ArrowShape shape, double thickness)
    {
        var r = Rect.Empty;
        if (shape.HasLine) { r.Union(shape.LineStart); r.Union(shape.LineEnd); }
        foreach (var poly in shape.Polygons) foreach (var p in poly) r.Union(p);
        if (r.IsEmpty) return r;
        r.Inflate(thickness / 2 + 1, thickness / 2 + 1);
        return r;
    }

    /// <summary>Hit test: within <paramref name="tolerance"/> of line or inside polygon.</summary>
    public static bool HitTest(ArrowShape shape, Point p, double tolerance)
    {
        if (shape.HasLine && DistanceToSegment(p, shape.LineStart, shape.LineEnd) <= tolerance) return true;
        foreach (var poly in shape.Polygons)
            if (Contains(poly, p) || DistanceToPolygon(p, poly) <= Math.Min(tolerance, 4)) return true;
        return false;
    }

    public static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double len2 = ab.LengthSquared;
        if (len2 < 1e-12) return (p - a).Length;
        double t = Math.Clamp(Vector.Multiply(p - a, ab) / len2, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    private static double DistanceToPolygon(Point p, Point[] poly)
    {
        double best = double.MaxValue;
        for (int i = 0; i < poly.Length; i++) best = Math.Min(best, DistanceToSegment(p, poly[i], poly[(i + 1) % poly.Length]));
        return best;
    }

    /// <summary>Point inside polygon (ray crossing parity).</summary>
    public static bool Contains(Point[] poly, Point p)
    {
        bool inside = false;
        for (int i = 0, j = poly.Length - 1; i < poly.Length; j = i++)
        {
            if ((poly[i].Y > p.Y) != (poly[j].Y > p.Y)
                && p.X < (poly[j].X - poly[i].X) * (p.Y - poly[i].Y) / (poly[j].Y - poly[i].Y) + poly[i].X)
                inside = !inside;
        }
        return inside;
    }
}

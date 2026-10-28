using System.Windows;

namespace Screeny.Annotations.Shapes;

/// <summary>
/// Геометрия стрелки без WPF-рендера: линия (если есть) и залитые многоугольники (наконечники или тело-клин).
/// </summary>
/// <param name="HasLine">Рисуется ли линия <paramref name="LineStart"/>→<paramref name="LineEnd"/> пером стиля.</param>
/// <param name="Dashed">Линия пунктирная.</param>
/// <param name="Polygons">Залитые многоугольники: наконечники (первая точка — острие) или тело «толстой» стрелки.</param>
public sealed record ArrowShape(bool HasLine, Point LineStart, Point LineEnd, bool Dashed, IReadOnlyList<Point[]> Polygons)
{
    public static readonly ArrowShape Empty = new(false, default, default, false, []);
}

public static class ArrowGeometry
{
    /// <summary>Длина наконечника обычной стрелки при данной толщине (у короткой стрелки — меньше).</summary>
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

    /// <summary>Обычная и пунктирная: линия до основания наконечника, чтобы она не торчала из острия.</summary>
    private static ArrowShape Single(Point start, Point end, Vector dir, double length, double thickness, bool dashed)
    {
        // Короткая стрелка — уменьшаем наконечник, чтобы он не «выворачивался» за начало линии.
        double head = Math.Min(HeadLength(thickness), length * 0.6);
        return new ArrowShape(true, start, end - dir * (head * 0.9), dashed, [Head(end, dir, head)]);
    }

    /// <summary>Двусторонняя: наконечники на обоих концах, каждый не длиннее 40 % стрелки.</summary>
    private static ArrowShape Double(Point start, Point end, Vector dir, double length, double thickness)
    {
        double head = Math.Min(HeadLength(thickness), length * 0.4);
        return new ArrowShape(true, start + dir * (head * 0.9), end - dir * (head * 0.9), false,
            [Head(end, dir, head), Head(start, -dir, head)]);
    }

    /// <summary>
    /// «Баннерная» стрелка одним многоугольником: узкий хвост в начале, тело расширяется к шейке,
    /// широкий наконечник. Порядок точек: хвост, шейка, крыло, острие, крыло, шейка, хвост.
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

    /// <summary>Треугольник наконечника: острие, левое и правое крыло.</summary>
    private static Point[] Head(Point tip, Vector dir, double head)
    {
        var basePt = tip - dir * head;
        var normal = new Vector(-dir.Y, dir.X) * (head * 0.8 / 2);
        return [tip, basePt - normal, basePt + normal];
    }

    /// <summary>Охват всех точек фигуры, расширенный на половину толщины линии.</summary>
    public static Rect Bounds(ArrowShape shape, double thickness)
    {
        var r = Rect.Empty;
        if (shape.HasLine) { r.Union(shape.LineStart); r.Union(shape.LineEnd); }
        foreach (var poly in shape.Polygons) foreach (var p in poly) r.Union(p);
        if (r.IsEmpty) return r;
        r.Inflate(thickness / 2 + 1, thickness / 2 + 1);
        return r;
    }

    /// <summary>Попадание: в пределах <paramref name="tolerance"/> от линии или внутри многоугольника.</summary>
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

    /// <summary>Точка внутри многоугольника (чётность пересечений луча).</summary>
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

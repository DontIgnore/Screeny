using System.Globalization;
using System.Windows;

namespace Screeny.Overlay;

/// <summary>Temporary ruler measurement in screenshot pixels: not in document, export or undo.</summary>
public readonly record struct RulerMeasurement(Point Start, Point End)
{
    public double Dx => End.X - Start.X;
    public double Dy => End.Y - Start.Y;
    public double Length => (End - Start).Length;

    /// <summary>“Δx 120 · Δy 45 · 128.2 px”: absolute offsets, integer; length one decimal.</summary>
    public string Label => string.Create(CultureInfo.InvariantCulture,
        $"Δx {Math.Abs(Math.Round(Dx)):0} · Δy {Math.Abs(Math.Round(Dy)):0} · {Length:0.#} px");

    /// <summary>End at 45° step from start (Shift), length preserved.</summary>
    public static Point Snap45(Point start, Point end)
    {
        var v = end - start;
        if (v.LengthSquared < 1e-12) return end;
        double angle = Math.Round(Math.Atan2(v.Y, v.X) / (Math.PI / 4)) * (Math.PI / 4);
        var snapped = start + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length;
        // Remove cos/sin noise: horizontal/vertical must give exactly 0 on other axis.
        return new Point(Math.Round(snapped.X, 6), Math.Round(snapped.Y, 6));
    }

    /// <summary>Measurement from <paramref name="start"/> to <paramref name="end"/>: points are integer pixel centers, Shift — 45°.</summary>
    public static RulerMeasurement Between(Point start, Point end, bool snap)
    {
        var a = RoundPoint(start);
        var b = RoundPoint(end);
        return new RulerMeasurement(a, snap ? Snap45(a, b) : b);
    }

    private static Point RoundPoint(Point p) => new(Math.Round(p.X), Math.Round(p.Y));
}

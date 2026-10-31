using System.Windows;

namespace Screeny.Themes;

/// <summary>
/// Window geometry on monitor — pure functions for <see cref="ChromeWindow"/>: height limit by work area of monitor
/// window actually on, and rectangle fully inside that area.
/// </summary>
public static class WindowFit
{
    /// <summary>Margin between window and work area edges, DIP.</summary>
    public const double Margin = 40;

    /// <summary>Below this window doesn't shrink unless monitor itself lower.</summary>
    public const double MinimumHeight = 360;

    /// <summary>
    /// Height limit, DIP: work area minus <see cref="Margin"/>, but no more than <paramref name="naturalCap"/>
    /// (scrollable window doesn't need to be tower on tall screen) and no less than <see cref="MinimumHeight"/> — if
    /// work area allows.
    /// </summary>
    public static double CapHeight(double workHeight, double naturalCap = double.PositiveInfinity)
    {
        double available = Math.Max(0, workHeight - Margin);
        double cap = Math.Min(available, naturalCap);
        return Math.Max(cap, Math.Min(MinimumHeight, Math.Max(0, workHeight)));
    }

    /// <summary>
    /// Window rectangle inside work area: size trimmed to area, position centered in area
    /// (<paramref name="center"/>) or nearest to previous without going outside edges.
    /// </summary>
    public static Rect FitInto(Rect window, Rect work, bool center)
    {
        double width = Math.Min(window.Width, work.Width), height = Math.Min(window.Height, work.Height);
        double x = center ? work.Left + (work.Width - width) / 2 : Math.Clamp(window.Left, work.Left, work.Right - width);
        double y = center ? work.Top + (work.Height - height) / 2 : Math.Clamp(window.Top, work.Top, work.Bottom - height);
        return new Rect(x, y, width, height);
    }

    /// <summary>“x,y,w,h” (integers, device pixels) → rectangle; null — not set, unparsable or empty size.</summary>
    public static Rect? ParseWorkArea(string? text)
    {
        var parts = text?.Split(',', StringSplitOptions.TrimEntries);
        if (parts is not { Length: 4 }) return null;
        var n = new int[4];
        for (int i = 0; i < 4; i++)
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out n[i])) return null;
        return n[2] > 0 && n[3] > 0 ? new Rect(n[0], n[1], n[2], n[3]) : null;
    }

    /// <summary>Device pixels → DIP at monitor scale <paramref name="scale"/> (1.0 = 96 DPI, 1.5 = 144 DPI).</summary>
    public static Rect ToDips(Rect pixels, double scale)
        => scale > 0 ? new Rect(pixels.X / scale, pixels.Y / scale, pixels.Width / scale, pixels.Height / scale) : pixels;
}

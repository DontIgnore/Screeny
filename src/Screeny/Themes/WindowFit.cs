using System.Windows;

namespace Screeny.Themes;

/// <summary>
/// Геометрия окна на мониторе — чистые функции для <see cref="ChromeWindow"/>: предел высоты по рабочей области
/// монитора, на котором окно на самом деле, и прямоугольник, целиком лежащий в этой области.
/// </summary>
public static class WindowFit
{
    /// <summary>Поле между окном и краями рабочей области, DIP.</summary>
    public const double Margin = 40;

    /// <summary>Меньше этого окно не сжимается, если только сам монитор не ниже.</summary>
    public const double MinimumHeight = 360;

    /// <summary>
    /// Предел высоты, DIP: рабочая область минус <see cref="Margin"/>, но не больше <paramref name="naturalCap"/>
    /// (окну с прокруткой незачем быть башней на высоком экране) и не меньше <see cref="MinimumHeight"/> — если
    /// рабочая область это позволяет.
    /// </summary>
    public static double CapHeight(double workHeight, double naturalCap = double.PositiveInfinity)
    {
        double available = Math.Max(0, workHeight - Margin);
        double cap = Math.Min(available, naturalCap);
        return Math.Max(cap, Math.Min(MinimumHeight, Math.Max(0, workHeight)));
    }

    /// <summary>
    /// Прямоугольник окна внутри рабочей области: размер урезается до области, положение — по центру области
    /// (<paramref name="center"/>) или ближайшее к прежнему, при котором окно не выходит за края.
    /// </summary>
    public static Rect FitInto(Rect window, Rect work, bool center)
    {
        double width = Math.Min(window.Width, work.Width), height = Math.Min(window.Height, work.Height);
        double x = center ? work.Left + (work.Width - width) / 2 : Math.Clamp(window.Left, work.Left, work.Right - width);
        double y = center ? work.Top + (work.Height - height) / 2 : Math.Clamp(window.Top, work.Top, work.Bottom - height);
        return new Rect(x, y, width, height);
    }

    /// <summary>«x,y,w,h» (целые, пиксели устройства) → прямоугольник; null — не задано, не разобрано или пустой размер.</summary>
    public static Rect? ParseWorkArea(string? text)
    {
        var parts = text?.Split(',', StringSplitOptions.TrimEntries);
        if (parts is not { Length: 4 }) return null;
        var n = new int[4];
        for (int i = 0; i < 4; i++)
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out n[i])) return null;
        return n[2] > 0 && n[3] > 0 ? new Rect(n[0], n[1], n[2], n[3]) : null;
    }

    /// <summary>Пиксели устройства → DIP при масштабе монитора <paramref name="scale"/> (1.0 = 96 DPI, 1.5 = 144 DPI).</summary>
    public static Rect ToDips(Rect pixels, double scale)
        => scale > 0 ? new Rect(pixels.X / scale, pixels.Y / scale, pixels.Width / scale, pixels.Height / scale) : pixels;
}

using System.Windows;

namespace Screeny.Overlay;

/// <summary>
/// Размещение HUD (всё в DIP): панель инструментов вокруг выделения и всплывающие панели параметров (flyout)
/// у якоря — кнопки панели или фигуры. Чистые функции: только прямоугольники и размеры.
/// </summary>
public static class HudLayout
{
    /// <summary>Зазор панели от выделения и от краёв рабочей области.</summary>
    public const double Gap = 8;
    /// <summary>Зазор flyout'а от якоря.</summary>
    public const double FlyoutGap = 6;

    /// <param name="Toolbar">Левый верхний угол панели.</param>
    /// <param name="Below">
    /// Панель под выделением: её внешняя сторона — нижняя, и flyout'ы кнопок открываются вниз. Над выделением
    /// или внутри у нижнего края — вверх.
    /// </param>
    public readonly record struct Placement(Point Toolbar, bool Below);

    /// <summary>
    /// Под выделением по левому краю; не влезает — над ним; нет места и там — внутри у нижнего края.
    /// Панель целиком остаётся в рабочей области (если она выше области — побеждает верхняя граница).
    /// </summary>
    public static Placement Place(Rect selection, Rect work, Size toolbar)
    {
        (double y, bool below)[] candidates =
        {
            (selection.Bottom + Gap, true),
            (selection.Top - Gap - toolbar.Height, false),
            (selection.Bottom - Gap - toolbar.Height, false),
        };
        var chosen = candidates[^1];
        foreach (var c in candidates)
        {
            if (c.y >= work.Top + Gap && c.y + toolbar.Height <= work.Bottom - Gap) { chosen = c; break; }
        }
        double y = Math.Max(work.Top + Gap, Math.Min(chosen.y, work.Bottom - Gap - toolbar.Height));
        double x = ClampInto(selection.Left, work.Left + Gap, work.Right - toolbar.Width - Gap);
        return new Placement(new Point(x, y), chosen.below);
    }

    /// <param name="TopLeft">Левый верхний угол flyout'а.</param>
    /// <param name="Below">Flyout под якорем (анимация вылетает сверху вниз); false — над якорем.</param>
    public readonly record struct FlyoutPlacement(Point TopLeft, bool Below);

    /// <inheritdoc cref="PlaceFlyout(Rect, Size, Rect, bool, Rect?)"/>
    public static FlyoutPlacement PlaceFlyout(Rect anchor, Size flyoutSize, Rect work, bool preferBelow)
        => PlaceFlyout(anchor, flyoutSize, work, preferBelow, null);

    /// <summary>
    /// Flyout у якоря: по центру якоря по горизонтали, с зазором <see cref="FlyoutGap"/> снизу (<paramref name="preferBelow"/>)
    /// или сверху. Не влезает в рабочую область с этой стороны или перекрывает <paramref name="avoid"/> (панель) —
    /// переворачивается на другую сторону. Не влезает нигде — встаёт туда, где места больше, и прижимается к области.
    /// По горизонтали всегда прижат к рабочей области с отступом <see cref="Gap"/>.
    /// </summary>
    /// <param name="anchor">Кнопка (по высоте — вся панель, чтобы flyout был снаружи панели) или рамка фигуры.</param>
    /// <param name="avoid">Что нельзя перекрывать (панель инструментов); null — ничего.</param>
    public static FlyoutPlacement PlaceFlyout(Rect anchor, Size flyoutSize, Rect work, bool preferBelow, Rect? avoid)
    {
        double w = flyoutSize.Width, h = flyoutSize.Height;
        double x = ClampInto(anchor.Left + anchor.Width / 2 - w / 2, work.Left + Gap, work.Right - Gap - w);
        double YFor(bool below) => below ? anchor.Bottom + FlyoutGap : anchor.Top - FlyoutGap - h;
        bool InWork(bool below) { double y = YFor(below); return y >= work.Top + Gap && y + h <= work.Bottom - Gap; }
        bool Clear(bool below) => avoid is not { } a || !Overlaps(new Rect(x, YFor(below), w, h), a);

        bool[] sides = [preferBelow, !preferBelow];
        foreach (var below in sides)
            if (InWork(below) && Clear(below)) return new FlyoutPlacement(new Point(x, YFor(below)), below);
        foreach (var below in sides)
            if (InWork(below)) return new FlyoutPlacement(new Point(x, YFor(below)), below);

        bool roomier = work.Bottom - anchor.Bottom >= anchor.Top - work.Top;
        double clamped = Math.Max(work.Top + Gap, Math.Min(YFor(roomier), work.Bottom - Gap - h));
        return new FlyoutPlacement(new Point(x, clamped), roomier);
    }

    /// <summary>Прямоугольники перекрываются по площади (касание краями — не перекрытие).</summary>
    public static bool Overlaps(Rect a, Rect b) => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    /// <summary>Clamp, при котором нижняя граница выигрывает, если элемент шире/выше доступного места.</summary>
    public static double ClampInto(double v, double min, double max) => Math.Max(min, Math.Min(v, max));
}

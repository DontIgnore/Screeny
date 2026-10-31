using System.Windows;

namespace Screeny.Overlay;

/// <summary>
/// HUD layout (all in DIP): toolbar around selection and options flyouts
/// at anchor — panel buttons or shape. Pure functions: only rectangles and sizes.
/// </summary>
public static class HudLayout
{
    /// <summary>Panel gap from selection and work area edges.</summary>
    public const double Gap = 8;
    /// <summary>Flyout gap from anchor.</summary>
    public const double FlyoutGap = 6;

    /// <param name="Toolbar">Panel top-left corner.</param>
    /// <param name="Below">
    /// Panel below selection: its outer side is bottom, button flyouts open down. Above selection
    /// or inside near bottom edge — up.
    /// </param>
    public readonly record struct Placement(Point Toolbar, bool Below);

    /// <summary>
    /// Below selection along left edge; doesn't fit — above; no place there — inside at bottom edge.
    /// Panel stays entirely in work area (if higher than area — top edge wins).
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

    /// <param name="TopLeft">Flyout top-left corner.</param>
    /// <param name="Below">Flyout below anchor (animation flies top-down); false — above.</param>
    public readonly record struct FlyoutPlacement(Point TopLeft, bool Below);

    /// <inheritdoc cref="PlaceFlyout(Rect, Size, Rect, bool, Rect?)"/>
    public static FlyoutPlacement PlaceFlyout(Rect anchor, Size flyoutSize, Rect work, bool preferBelow)
        => PlaceFlyout(anchor, flyoutSize, work, preferBelow, null);

    /// <summary>
    /// Flyout at anchor: centered horizontally, gap <see cref="FlyoutGap"/> below (<paramref name="preferBelow"/>)
    /// or above. Doesn't fit work area on that side or overlaps <paramref name="avoid"/> (panel) —
    /// flipped to other side. Fits nowhere — placed where more space, pressed to area.
    /// Horizontally always pressed to work area with <see cref="Gap"/> margin.
    /// </summary>
    /// <param name="anchor">Button (height — whole panel, so flyout outside panel) or shape border.</param>
    /// <param name="avoid">What cannot be overlapped (toolbar); null — nothing.</param>
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

    /// <summary>Rectangles overlap by area (edge touch not overlap).</summary>
    public static bool Overlaps(Rect a, Rect b) => a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    /// <summary>Clamp where bottom edge wins if element wider/taller than available space.</summary>
    public static double ClampInto(double v, double min, double max) => Math.Max(min, Math.Min(v, max));
}

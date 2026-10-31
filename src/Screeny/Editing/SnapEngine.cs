using System.Windows;

namespace Screeny.Editing;

/// <summary>Guide: vertical — line <c>x = Position</c> from <c>Start</c> to <c>End</c> along Y, horizontal — vice versa.</summary>
public enum GuideOrientation { Vertical, Horizontal }

/// <summary>Thin guide line in screenshot pixels: shows what edge or center snapped to.</summary>
public readonly record struct SnapGuide(GuideOrientation Orientation, double Position, double Start, double End);

/// <param name="Offset">Correction to moving object position (per axis — 0 if no snap).</param>
/// <param name="Guides">Guides for matched edges and centers after correction.</param>
public sealed record SnapResult(Vector Offset, IReadOnlyList<SnapGuide> Guides)
{
    public static SnapResult None { get; } = new(default, []);
}

/// <summary>
/// Edge/center snapping (smart guides), pure logic without UI. Per axis independently compares
/// left edge, center and right edge of moving rectangle (Y: top, center, bottom) with same lines
/// of targets; nearest pair within threshold gives correction. Point is zero-size rectangle.
/// </summary>
public static class SnapEngine
{
    /// <summary>Line match after correction (double error).</summary>
    private const double Same = 1e-3;

    public static SnapResult Snap(Rect moving, IReadOnlyList<Rect> targets, double threshold)
    {
        if (moving.IsEmpty || targets.Count == 0 || !(threshold > 0)) return SnapResult.None;
        double? dx = Nearest(moving, targets, vertical: true, threshold);
        double? dy = Nearest(moving, targets, vertical: false, threshold);
        if (dx is null && dy is null) return SnapResult.None;

        var snapped = Rect.Offset(moving, dx ?? 0, dy ?? 0);
        var guides = new List<SnapGuide>();
        if (dx is not null) AddGuides(guides, snapped, targets, vertical: true);
        if (dy is not null) AddGuides(guides, snapped, targets, vertical: false);
        return new SnapResult(new Vector(dx ?? 0, dy ?? 0), guides);
    }

    /// <summary>Point snaps same as zero-size rectangle.</summary>
    public static SnapResult SnapPoint(Point p, IReadOnlyList<Rect> targets, double threshold) => Snap(new Rect(p, p), targets, threshold);

    /// <summary>Rectangle lines per axis: vertical (X) — left, center, right; horizontal (Y) — top, center, bottom.</summary>
    private static void Lines(Rect r, bool vertical, Span<double> lines)
    {
        double start = vertical ? r.Left : r.Top;
        double length = vertical ? r.Width : r.Height;
        lines[0] = start;
        lines[1] = start + length / 2;
        lines[2] = vertical ? r.Right : r.Bottom;
    }

    /// <summary>Smallest by absolute value correction aligning any object line with any target line; null — nothing within threshold.</summary>
    private static double? Nearest(Rect moving, IReadOnlyList<Rect> targets, bool vertical, double threshold)
    {
        Span<double> movingLines = stackalloc double[3];
        Span<double> targetLines = stackalloc double[3];
        Lines(moving, vertical, movingLines);
        double? best = null;
        foreach (var t in targets)
        {
            if (t.IsEmpty) continue;
            Lines(t, vertical, targetLines);
            foreach (var v in targetLines)
                foreach (var m in movingLines)
                {
                    double d = v - m;
                    if (Math.Abs(d) <= threshold && (best is null || Math.Abs(d) < Math.Abs(best.Value))) best = d;
                }
        }
        return best;
    }

    /// <summary>
    /// Guide for each matched line: through common span of object and all targets with that line.
    /// One position — one guide (point's left, center, right coincide).
    /// </summary>
    private static void AddGuides(List<SnapGuide> guides, Rect snapped, IReadOnlyList<Rect> targets, bool vertical)
    {
        var orientation = vertical ? GuideOrientation.Vertical : GuideOrientation.Horizontal;
        Span<double> movingLines = stackalloc double[3];
        Span<double> targetLines = stackalloc double[3];
        Lines(snapped, vertical, movingLines);
        foreach (var m in movingLines)
        {
            if (guides.Any(g => g.Orientation == orientation && Math.Abs(g.Position - m) < Same)) continue;
            double start = vertical ? snapped.Top : snapped.Left, end = vertical ? snapped.Bottom : snapped.Right;
            bool matched = false;
            foreach (var t in targets)
            {
                if (t.IsEmpty) continue;
                Lines(t, vertical, targetLines);
                if (!(Math.Abs(targetLines[0] - m) < Same || Math.Abs(targetLines[1] - m) < Same || Math.Abs(targetLines[2] - m) < Same)) continue;
                matched = true;
                start = Math.Min(start, vertical ? t.Top : t.Left);
                end = Math.Max(end, vertical ? t.Bottom : t.Right);
            }
            if (matched) guides.Add(new SnapGuide(orientation, m, start, end));
        }
    }
}

/// <summary>
/// Snapping for duration of one tool gesture: targets (edges and centers of other document shapes and region)
/// collected at gesture start, threshold and “enabled” asked from host on each move — so <c>Alt</c>
/// mid-drag disables snapping immediately. Current guides in <see cref="Guides"/>.
/// </summary>
/// <param name="enabled">Setting enabled and <c>Alt</c> not held.</param>
/// <param name="threshold">Threshold in screenshot pixels (6 screen pixels at current scale).</param>
/// <param name="area">Screenshot region (overlay selection, editor canvas); empty — without it.</param>
public sealed class Snapper(Func<bool> enabled, Func<double> threshold, Func<Rect> area)
{
    private Rect[] _targets = [];

    /// <summary>Guides of current snap; empty — nothing snapped or no gesture.</summary>
    public IReadOnlyList<SnapGuide> Guides { get; private set; } = [];
    public event Action? GuidesChanged;

    /// <summary>Gesture start: targets — all document shapes except <paramref name="exclude"/> (moving) and area.</summary>
    public void Begin(Screeny.Annotations.AnnotationDocument doc, Screeny.Annotations.Annotation? exclude = null)
        => Begin(doc.Items.Where(a => !ReferenceEquals(a, exclude)).Select(a => a.GeometryBounds));

    /// <summary>Gesture start with prepared target list (area added automatically).</summary>
    public void Begin(IEnumerable<Rect> others)
    {
        var list = others.Where(r => !r.IsEmpty).ToList();
        var a = area();
        if (!a.IsEmpty && a.Width > 0 && a.Height > 0) list.Add(a);
        _targets = list.ToArray();
        SetGuides([]);
    }

    /// <summary>Correction for moving rectangle (0 if snapping disabled or no target found).</summary>
    public Vector SnapRect(Rect moving)
    {
        if (!enabled()) { SetGuides([]); return default; }
        var r = SnapEngine.Snap(moving, _targets, threshold());
        SetGuides(r.Guides);
        return r.Offset;
    }

    /// <summary>Point (corner of drawing shape, line end, text position) snapped to target lines.</summary>
    public Point SnapPoint(Point p) => p + SnapRect(new Rect(p, p));

    /// <summary>Remove guides without ending gesture (Shift: square and 45° step beat snapping).</summary>
    public void Hide() => SetGuides([]);

    /// <summary>Gesture end: guides disappear.</summary>
    public void End()
    {
        _targets = [];
        SetGuides([]);
    }

    private void SetGuides(IReadOnlyList<SnapGuide> guides)
    {
        if (guides.Count == 0 && Guides.Count == 0) return;
        if (guides.SequenceEqual(Guides)) return;
        Guides = guides;
        GuidesChanged?.Invoke();
    }
}

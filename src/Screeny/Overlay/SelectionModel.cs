namespace Screeny.Overlay;

public sealed class SelectionModel
{
    public PixelRect Bounds { get; }
    public PixelRect Rect { get; set; }
    public bool HasSelection => !Rect.IsEmpty;

    private Handle _dragHandle = Handle.None;
    private PixelPoint _dragStart;
    private PixelRect _dragOrigin;

    public SelectionModel(PixelRect bounds) => Bounds = bounds;

    /// <summary>Rectangle by two drag points; final pixel included.</summary>
    public static PixelRect FromPoints(PixelPoint a, PixelPoint b)
        => PixelRect.FromCorners(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X) + 1, Math.Max(a.Y, b.Y) + 1);

    /// <summary>
    /// Same, but dragged edges (from cursor side <paramref name="b"/>) snap via <paramref name="snap"/>.
    /// Snap that would collapse rectangle not applied.
    /// </summary>
    public static PixelRect FromPoints(PixelPoint a, PixelPoint b, EdgeSnap? snap)
    {
        var r = FromPoints(a, b);
        if (snap is null) return r;
        int x1 = r.X, x2 = r.Right, y1 = r.Y, y2 = r.Bottom;
        if (b.X >= a.X) x2 = SnapIfValid(snap, EdgeAxis.Vertical, x2, b.Y, lo: x1 + 1, hi: int.MaxValue);
        else x1 = SnapIfValid(snap, EdgeAxis.Vertical, x1, b.Y, lo: int.MinValue, hi: x2 - 1);
        if (b.Y >= a.Y) y2 = SnapIfValid(snap, EdgeAxis.Horizontal, y2, b.X, lo: y1 + 1, hi: int.MaxValue);
        else y1 = SnapIfValid(snap, EdgeAxis.Horizontal, y1, b.X, lo: int.MinValue, hi: y2 - 1);
        return PixelRect.FromCorners(x1, y1, x2, y2);
    }

    private static int SnapIfValid(EdgeSnap snap, EdgeAxis axis, int edge, int cross, int lo, int hi)
        => snap(axis, edge, cross) is { } s && s >= lo && s <= hi ? s : edge;

    public void Nudge(int dx, int dy)
    {
        var r = Rect.Offset(dx, dy);
        int x = Math.Clamp(r.X, Bounds.X, Bounds.Right - r.Width);
        int y = Math.Clamp(r.Y, Bounds.Y, Bounds.Bottom - r.Height);
        Rect = new PixelRect(x, y, r.Width, r.Height);
    }

    public void Resize(int dw, int dh)
    {
        int w = Math.Clamp(Rect.Width + dw, 1, Bounds.Right - Rect.X);
        int h = Math.Clamp(Rect.Height + dh, 1, Bounds.Bottom - Rect.Y);
        Rect = new PixelRect(Rect.X, Rect.Y, w, h);
    }

    public Handle HitTest(PixelPoint p, int tolerance)
    {
        if (!HasSelection) return Handle.None;
        bool nearL = Math.Abs(p.X - Rect.X) <= tolerance, nearR = Math.Abs(p.X - Rect.Right) <= tolerance;
        bool nearT = Math.Abs(p.Y - Rect.Y) <= tolerance, nearB = Math.Abs(p.Y - Rect.Bottom) <= tolerance;
        bool inX = p.X >= Rect.X - tolerance && p.X <= Rect.Right + tolerance;
        bool inY = p.Y >= Rect.Y - tolerance && p.Y <= Rect.Bottom + tolerance;
        if (nearL && nearT) return Handle.TopLeft;
        if (nearR && nearT) return Handle.TopRight;
        if (nearR && nearB) return Handle.BottomRight;
        if (nearL && nearB) return Handle.BottomLeft;
        if (nearT && inX) return Handle.Top;
        if (nearB && inX) return Handle.Bottom;
        if (nearL && inY) return Handle.Left;
        if (nearR && inY) return Handle.Right;
        return Rect.Contains(p) ? Handle.Move : Handle.None;
    }

    public void BeginDrag(Handle handle, PixelPoint at) { _dragHandle = handle; _dragStart = at; _dragOrigin = Rect; }

    /// <summary>
    /// Handle or edge drag. With <paramref name="snap"/> dragged edges snap (window edges, contrast
    /// edges), except edge pressed to screenshot boundary. Whole rectangle move doesn't snap.
    /// </summary>
    public void DragTo(PixelPoint p, EdgeSnap? snap = null)
    {
        int dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;
        var o = _dragOrigin;
        int x1 = o.X, y1 = o.Y, x2 = o.Right, y2 = o.Bottom;
        switch (_dragHandle)
        {
            case Handle.Move: Rect = o; Nudge(dx, dy); return;
            case Handle.TopLeft: x1 += dx; y1 += dy; break;
            case Handle.Top: y1 += dy; break;
            case Handle.TopRight: x2 += dx; y1 += dy; break;
            case Handle.Right: x2 += dx; break;
            case Handle.BottomRight: x2 += dx; y2 += dy; break;
            case Handle.Bottom: y2 += dy; break;
            case Handle.BottomLeft: x1 += dx; y2 += dy; break;
            case Handle.Left: x1 += dx; break;
            default: return;
        }
        // Cursor at very screenshot edge presses dragged edge to boundary: usually grabbed by border line
        // (pixel Right/Bottom or 1 px outside), without this delta would not bring edge to last
        // column/row (cursor never beyond Bounds.Right − 1) or zero.
        bool movesX1 = _dragHandle is Handle.TopLeft or Handle.Left or Handle.BottomLeft;
        bool movesX2 = _dragHandle is Handle.TopRight or Handle.Right or Handle.BottomRight;
        bool movesY1 = _dragHandle is Handle.TopLeft or Handle.Top or Handle.TopRight;
        bool movesY2 = _dragHandle is Handle.BottomLeft or Handle.Bottom or Handle.BottomRight;
        if (movesX1) x1 = SnapToEdge(x1, p.X, Bounds.X, Bounds.Right);
        if (movesX2) x2 = SnapToEdge(x2, p.X, Bounds.X, Bounds.Right);
        if (movesY1) y1 = SnapToEdge(y1, p.Y, Bounds.Y, Bounds.Bottom);
        if (movesY2) y2 = SnapToEdge(y2, p.Y, Bounds.Y, Bounds.Bottom);
        if (snap is not null)
        {
            int sx1 = movesX1 ? SnapInside(snap, EdgeAxis.Vertical, x1, p.Y, Bounds.X, Bounds.Right) : x1;
            int sx2 = movesX2 ? SnapInside(snap, EdgeAxis.Vertical, x2, p.Y, Bounds.X, Bounds.Right) : x2;
            int sy1 = movesY1 ? SnapInside(snap, EdgeAxis.Horizontal, y1, p.X, Bounds.Y, Bounds.Bottom) : y1;
            int sy2 = movesY2 ? SnapInside(snap, EdgeAxis.Horizontal, y2, p.X, Bounds.Y, Bounds.Bottom) : y2;
            // Snap must not collapse or invert narrow rectangle.
            if (Math.Sign(sx2 - sx1) == Math.Sign(x2 - x1) && sx2 != sx1) (x1, x2) = (sx1, sx2);
            if (Math.Sign(sy2 - sy1) == Math.Sign(y2 - y1) && sy2 != sy1) (y1, y2) = (sy1, sy2);
        }

        var (x, w) = Axis(x1, x2, Bounds.X, Bounds.Right);
        var (y, h) = Axis(y1, y2, Bounds.Y, Bounds.Bottom);
        Rect = new PixelRect(x, y, w, h);
    }

    /// <summary>Edge at screenshot boundary stays there; else — where snapped (if snapped inside screenshot).</summary>
    private static int SnapInside(EdgeSnap snap, EdgeAxis axis, int edge, int cross, int min, int max)
        => edge <= min || edge >= max ? edge : snap(axis, edge, cross) is { } s && s >= min && s <= max ? s : edge;

    private static int SnapToEdge(int edge, int cursor, int min, int max)
        => cursor <= min ? min : cursor >= max - 1 ? max : edge;

    /// <summary>
    /// Segment [a, b) (any order) clamped to [min, max). Collapsed axis gets size 1,
    /// not affecting other. Exclusive edge may equal max — so last column/row reached.
    /// </summary>
    private static (int Start, int Length) Axis(int a, int b, int min, int max)
    {
        int lo = Math.Clamp(Math.Min(a, b), min, max), hi = Math.Clamp(Math.Max(a, b), min, max);
        if (hi > lo) return (lo, hi - lo);
        return (Math.Clamp(lo, min, max - 1), 1);
    }

    public void EndDrag() => _dragHandle = Handle.None;
}

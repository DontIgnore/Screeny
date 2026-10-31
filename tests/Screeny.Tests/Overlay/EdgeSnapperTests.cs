using Screeny.Overlay;

namespace Screeny.Tests.Overlay;

public class EdgeSnapperTests
{
    private const int W = 200, H = 120;

    /// <summary>Synthetic Bgra32: pixel brightness by function (gray).</summary>
    private static EdgeSnapper Image(Func<int, int, int> luma)
    {
        var px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                byte v = (byte)Math.Clamp(luma(x, y), 0, 255);
                px[i] = px[i + 1] = px[i + 2] = v;
                px[i + 3] = 255;
            }
        return EdgeSnapper.FromBgra(px, W, H, W * 4);
    }

    [Fact]
    public void SharpVerticalEdgeNearby_Snaps()
    {
        // Light left, dark from x = 100: boundary between columns 99 and 100 — position 100.
        var s = Image((x, _) => x < 100 ? 230 : 40);
        Assert.Equal(100, s.Find(EdgeAxis.Vertical, 95, 60, 8));
        Assert.Equal(100, s.Find(EdgeAxis.Vertical, 107, 60, 8));
        Assert.Equal(100, s.Find(EdgeAxis.Vertical, 100, 60, 8));
    }

    [Fact]
    public void SharpHorizontalEdgeNearby_Snaps()
    {
        var s = Image((_, y) => y < 50 ? 30 : 200);
        Assert.Equal(50, s.Find(EdgeAxis.Horizontal, 46, 100, 8));
    }

    [Fact]
    public void EdgeBeyondThreshold_DoesNotSnap()
    {
        var s = Image((x, _) => x < 100 ? 230 : 40);
        Assert.Null(s.Find(EdgeAxis.Vertical, 91, 60, 8));
        Assert.Null(s.Find(EdgeAxis.Vertical, 109, 60, 8));
    }

    [Fact]
    public void Noise_DoesNotSnap()
    {
        var rng = new Random(42);
        var noise = new int[W * H];
        for (int i = 0; i < noise.Length; i++) noise[i] = 128 + rng.Next(-40, 41);
        var s = Image((x, y) => noise[y * W + x]);
        for (int x = 10; x < W - 10; x += 7) Assert.Null(s.Find(EdgeAxis.Vertical, x, 60, 8));
    }

    [Fact]
    public void SmoothGradient_DoesNotSnap()
    {
        var s = Image((x, _) => x * 255 / W);
        Assert.Null(s.Find(EdgeAxis.Vertical, 100, 60, 8));
    }

    [Fact]
    public void TwoEdges_NearestWins()
    {
        // Boundaries at 96 and 103; cursor at 101 — closer 103.
        var s = Image((x, _) => x < 96 ? 220 : x < 103 ? 120 : 20);
        Assert.Equal(103, s.Find(EdgeAxis.Vertical, 101, 60, 8));
        Assert.Equal(96, s.Find(EdgeAxis.Vertical, 98, 60, 8));
    }

    [Fact]
    public void BlurredTwoPixelEdge_GivesSinglePosition()
    {
        // 230 | 135 | 40: two equal steps — one position, not jitter between neighbors.
        var s = Image((x, _) => x < 100 ? 230 : x == 100 ? 135 : 40);
        var a = s.Find(EdgeAxis.Vertical, 97, 60, 8);
        var b = s.Find(EdgeAxis.Vertical, 104, 60, 8);
        Assert.NotNull(a);
        Assert.Equal(a, b);
        Assert.InRange(a!.Value, 100, 101);
    }

    [Fact]
    public void EdgeOnlyAcrossPartOfBand_IsNotStable()
    {
        // Boundary only in 10 of 41 rows (text, small detail) — no snap.
        var s = Image((x, y) => y >= 55 && y < 65 && x >= 100 ? 40 : 230);
        Assert.Null(s.Find(EdgeAxis.Vertical, 98, 60, 8));
    }

    [Fact]
    public void LowContrastEdge_IsIgnored()
    {
        var s = Image((x, _) => x < 100 ? 130 : 120);
        Assert.Null(s.Find(EdgeAxis.Vertical, 100, 60, 8));
    }

    [Fact]
    public void BandNearImageBorder_IsClipped()
    {
        var s = Image((x, _) => x < 100 ? 230 : 40);
        Assert.Equal(100, s.Find(EdgeAxis.Vertical, 97, 0, 8));
        Assert.Equal(100, s.Find(EdgeAxis.Vertical, 97, H - 1, 8));
    }
}

public class SelectionSnapTests
{
    private static readonly PixelRect Bounds = new(0, 0, 400, 300);

    /// <summary>Stub: vertical boundary at 100, horizontal at 50, threshold 8.</summary>
    private static int? Snap(EdgeAxis axis, int edge, int cross)
    {
        int target = axis == EdgeAxis.Vertical ? 100 : 50;
        return Math.Abs(edge - target) <= 8 ? target : null;
    }

    [Fact]
    public void NewFrame_DraggedRightDown_RightAndBottomEdgesSnap()
    {
        // Cursor at pixel 96 → right edge 97 → snaps to boundary 100 (includes column 99).
        var r = SelectionModel.FromPoints(new PixelPoint(10, 10), new PixelPoint(96, 45), Snap);
        Assert.Equal(new PixelRect(10, 10, 90, 40), r);
    }

    [Fact]
    public void NewFrame_DraggedLeftUp_LeftAndTopEdgesSnap()
    {
        var r = SelectionModel.FromPoints(new PixelPoint(300, 200), new PixelPoint(104, 53), Snap);
        Assert.Equal(new PixelRect(100, 50, 201, 151), r);
    }

    [Fact]
    public void NewFrame_WithoutSnap_IsUnchanged()
        => Assert.Equal(SelectionModel.FromPoints(new(10, 10), new(96, 45)), SelectionModel.FromPoints(new(10, 10), new(96, 45), null));

    [Fact]
    public void HandleDrag_RightEdgeSnaps()
    {
        var m = new SelectionModel(Bounds) { Rect = new PixelRect(10, 10, 50, 50) };
        m.BeginDrag(Handle.Right, new PixelPoint(60, 30));
        m.DragTo(new PixelPoint(95, 30), Snap);
        Assert.Equal(new PixelRect(10, 10, 90, 50), m.Rect);
    }

    [Fact]
    public void HandleDrag_CornerSnapsBothEdges()
    {
        var m = new SelectionModel(Bounds) { Rect = new PixelRect(200, 200, 50, 50) };
        m.BeginDrag(Handle.TopLeft, new PixelPoint(200, 200));
        m.DragTo(new PixelPoint(105, 46), Snap);
        Assert.Equal(new PixelRect(100, 50, 150, 200), m.Rect);
    }

    [Fact]
    public void HandleDrag_MoveWholeFrame_DoesNotSnap()
    {
        var m = new SelectionModel(Bounds) { Rect = new PixelRect(10, 10, 50, 50) };
        m.BeginDrag(Handle.Move, new PixelPoint(30, 30));
        m.DragTo(new PixelPoint(115, 30), Snap); // left edge 95 would be within threshold of 100
        Assert.Equal(new PixelRect(95, 10, 50, 50), m.Rect);
    }

    [Fact]
    public void HandleDrag_EdgeAtImageBorder_StaysThere()
    {
        // Boundary 395 near screenshot edge 400: edge pressed to screenshot edge doesn't leave.
        int? nearBorder(EdgeAxis axis, int edge, int cross) => Math.Abs(edge - 395) <= 8 ? 395 : null;
        var m = new SelectionModel(Bounds) { Rect = new PixelRect(10, 10, 50, 50) };
        m.BeginDrag(Handle.Right, new PixelPoint(60, 30));
        m.DragTo(new PixelPoint(399, 30), nearBorder);
        Assert.Equal(400, m.Rect.Right);
    }

    [Fact]
    public void HandleDrag_SnapNeverCollapsesFrame()
    {
        // Narrow border: snapping right edge past left not applied.
        int? behind(EdgeAxis axis, int edge, int cross) => axis == EdgeAxis.Vertical ? 100 : null;
        var m = new SelectionModel(Bounds) { Rect = new PixelRect(102, 10, 3, 50) };
        m.BeginDrag(Handle.Right, new PixelPoint(105, 30));
        m.DragTo(new PixelPoint(106, 30), behind);
        Assert.Equal(new PixelRect(102, 10, 4, 50), m.Rect);
    }

    [Fact]
    public void ArrowNudgeAndResize_NeverSnap()
    {
        var m = new SelectionModel(Bounds) { Rect = new PixelRect(95, 10, 3, 3) };
        m.Nudge(1, 0);
        m.Resize(1, 0);
        Assert.Equal(new PixelRect(96, 10, 4, 3), m.Rect);
    }
}

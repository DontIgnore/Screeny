using Screeny.Overlay;

namespace Screeny.Tests.Overlay;

public class SelectionModelTests
{
    private static readonly PixelRect Bounds = new(0, 0, 1920, 1080);

    [Fact]
    public void FromPoints_NormalizesReversedDrag()
    {
        var r = SelectionModel.FromPoints(new PixelPoint(100, 200), new PixelPoint(50, 20));
        Assert.Equal(new PixelRect(50, 20, 51, 181), r); // inclusive end pixel
    }

    [Fact]
    public void FromPoints_SinglePixelClick_IsOnePixel()
    {
        var r = SelectionModel.FromPoints(new PixelPoint(10, 10), new PixelPoint(10, 10));
        Assert.Equal(new PixelRect(10, 10, 1, 1), r);
    }

    [Fact]
    public void Clamp_KeepsInsideBounds()
    {
        var r = new PixelRect(-20, 1000, 100, 200).Clamp(Bounds);
        Assert.Equal(new PixelRect(0, 1000, 80, 80), r);
    }

    [Fact]
    public void Nudge_MovesButStaysInside()
    {
        var m = new SelectionModel(Bounds) { Rect = new PixelRect(0, 0, 100, 100) };
        m.Nudge(-5, 10);
        Assert.Equal(new PixelRect(0, 10, 100, 100), m.Rect);
    }

    [Fact]
    public void Resize_GrowsFromBottomRight_AndRespectsMinimum()
    {
        var m = new SelectionModel(Bounds) { Rect = new PixelRect(10, 10, 100, 100) };
        m.Resize(5, -99);
        Assert.Equal(new PixelRect(10, 10, 105, 1), m.Rect);
    }

    [Theory]
    [InlineData(10, 10, Handle.TopLeft)]
    [InlineData(110, 110, Handle.BottomRight)]
    [InlineData(60, 10, Handle.Top)]
    [InlineData(10, 60, Handle.Left)]
    [InlineData(60, 60, Handle.Move)]
    [InlineData(300, 300, Handle.None)]
    public void HitTest_ReturnsHandle(int x, int y, Handle expected)
    {
        var m = new SelectionModel(Bounds) { Rect = new PixelRect(10, 10, 100, 100) };
        Assert.Equal(expected, m.HitTest(new PixelPoint(x, y), tolerance: 6));
    }

    [Fact]
    public void DragHandle_TopLeft_MovesOrigin()
    {
        var m = new SelectionModel(Bounds) { Rect = new PixelRect(10, 10, 100, 100) };
        m.BeginDrag(Handle.TopLeft, new PixelPoint(10, 10));
        m.DragTo(new PixelPoint(20, 30));
        Assert.Equal(new PixelRect(20, 30, 90, 80), m.Rect);
    }

    [Fact]
    public void DragHandle_PastOppositeEdge_FlipsInsteadOfNegative()
    {
        var m = new SelectionModel(Bounds) { Rect = new PixelRect(10, 10, 100, 100) };
        m.BeginDrag(Handle.Right, new PixelPoint(110, 50));
        m.DragTo(new PixelPoint(0, 50));
        Assert.Equal(new PixelRect(0, 10, 10, 100), m.Rect);
    }

    [Fact]
    public void DragHandle_CollapseOneAxis_KeepsOther()
    {
        var m = new SelectionModel(Bounds) { Rect = new PixelRect(10, 10, 100, 100) };
        m.BeginDrag(Handle.Right, new PixelPoint(110, 50));
        m.DragTo(new PixelPoint(10, 50));
        Assert.Equal(new PixelRect(10, 10, 1, 100), m.Rect);
    }

    [Fact]
    public void DragHandle_CollapseBothAxes_IsOnePixel()
    {
        var m = new SelectionModel(Bounds) { Rect = new PixelRect(10, 10, 100, 100) };
        m.BeginDrag(Handle.BottomRight, new PixelPoint(110, 110));
        m.DragTo(new PixelPoint(10, 10));
        Assert.Equal(new PixelRect(10, 10, 1, 1), m.Rect);
    }

    [Fact]
    public void DragHandle_Right_ReachesLastColumn()
    {
        // Model accepts exclusive right edge equal Bounds.Right.
        var m = new SelectionModel(Bounds) { Rect = new PixelRect(10, 10, 100, 100) };
        m.BeginDrag(Handle.Right, new PixelPoint(110, 50));
        m.DragTo(new PixelPoint(Bounds.Right, 50));
        Assert.Equal(Bounds.Right, m.Rect.Right);
        m.DragTo(new PixelPoint(Bounds.Right + 50, 50)); // beyond edge — clamped
        Assert.Equal(Bounds.Right, m.Rect.Right);
    }

    [Fact]
    public void DragHandle_Bottom_ReachesLastRow()
    {
        var m = new SelectionModel(Bounds) { Rect = new PixelRect(10, 10, 100, 100) };
        m.BeginDrag(Handle.Bottom, new PixelPoint(50, 110));
        m.DragTo(new PixelPoint(50, Bounds.Bottom));
        Assert.Equal(Bounds.Bottom, m.Rect.Bottom);
        Assert.Equal(10, m.Rect.Y);
    }

    [Fact]
    public void DragHandle_Right_GrabbedOnBorder_ReachesLastColumnAtLastCursorPixel()
    {
        // Border drawn on pixel Right, grabbed there; cursor can't go beyond Bounds.Right − 1.
        var m = new SelectionModel(Bounds) { Rect = new PixelRect(10, 10, 100, 100) };
        m.BeginDrag(Handle.Right, new PixelPoint(110, 50));
        m.DragTo(new PixelPoint(Bounds.Right - 1, 50));
        Assert.Equal(new PixelRect(10, 10, Bounds.Right - 10, 100), m.Rect);
    }

    [Fact]
    public void DragHandle_BottomRight_GrabbedOnBorder_ReachesLastRowAndColumn()
    {
        var m = new SelectionModel(Bounds) { Rect = new PixelRect(10, 10, 100, 100) };
        m.BeginDrag(Handle.BottomRight, new PixelPoint(110, 110));
        m.DragTo(new PixelPoint(Bounds.Right - 1, Bounds.Bottom - 1));
        Assert.Equal(Bounds.Right, m.Rect.Right);
        Assert.Equal(Bounds.Bottom, m.Rect.Bottom);
    }

    [Fact]
    public void DragHandle_Left_GrabbedOutside_ReachesFirstColumn()
    {
        var m = new SelectionModel(Bounds) { Rect = new PixelRect(10, 10, 100, 100) };
        m.BeginDrag(Handle.Left, new PixelPoint(9, 50));
        m.DragTo(new PixelPoint(0, 50));
        Assert.Equal(new PixelRect(0, 10, 110, 100), m.Rect);
    }

    [Fact]
    public void DragHandle_Top_GrabbedOutside_ReachesFirstRow()
    {
        var m = new SelectionModel(Bounds) { Rect = new PixelRect(10, 10, 100, 100) };
        m.BeginDrag(Handle.Top, new PixelPoint(50, 8));
        m.DragTo(new PixelPoint(50, 0));
        Assert.Equal(new PixelRect(10, 0, 100, 110), m.Rect);
    }
}


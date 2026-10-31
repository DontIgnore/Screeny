using Screeny.Capture;
using Screeny.Overlay;

namespace Screeny.Tests.Capture;

public class WindowFinderTests
{
    [Fact]
    public void PickTopmostAtPoint_ReturnsFirstInZOrderContainingPoint()
    {
        var windows = new List<WindowInfo>
        {
            new(new IntPtr(1), new PixelRect(100, 100, 200, 200)),
            new(new IntPtr(2), new PixelRect(0, 0, 1000, 1000)),
        };
        Assert.Equal(new IntPtr(1), WindowFinder.PickTopmostAt(windows, new PixelPoint(150, 150))?.Handle);
        Assert.Equal(new IntPtr(2), WindowFinder.PickTopmostAt(windows, new PixelPoint(50, 50))?.Handle);
        Assert.Null(WindowFinder.PickTopmostAt(windows, new PixelPoint(2000, 2000)));
    }

    [Fact]
    public void VisibleEdges_OnCursorLine_ExcludeOccludedAndOffLineWindows()
    {
        var windows = new List<WindowInfo>
        {
            new(new IntPtr(1), new PixelRect(300, 100, 200, 200)), // top: covers right edge of second window
            new(new IntPtr(2), new PixelRect(100, 50, 300, 300)),  // edges 100 and 400 on X; 400 under first window
            new(new IntPtr(3), new PixelRect(600, 500, 100, 100)), // height doesn't cover row 200
        };
        Assert.Equal([300, 500, 100], WindowFinder.VisibleEdges(windows, EdgeAxis.Vertical, 200).ToArray());
        // Row 60: first window absent — right edge of second (400) visible.
        Assert.Equal([100, 400], WindowFinder.VisibleEdges(windows, EdgeAxis.Vertical, 60).ToArray());
        // Horizontal edges on column 150: only second window, both edges visible.
        Assert.Equal([50, 350], WindowFinder.VisibleEdges(windows, EdgeAxis.Horizontal, 150).ToArray());
    }
}

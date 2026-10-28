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
            new(new IntPtr(1), new PixelRect(300, 100, 200, 200)), // сверху: закрывает правый край второго окна
            new(new IntPtr(2), new PixelRect(100, 50, 300, 300)),  // края 100 и 400 по X; 400 под первым окном
            new(new IntPtr(3), new PixelRect(600, 500, 100, 100)), // по высоте не накрывает строку 200
        };
        Assert.Equal([300, 500, 100], WindowFinder.VisibleEdges(windows, EdgeAxis.Vertical, 200).ToArray());
        // Строка 60: первого окна там нет — правый край второго (400) виден.
        Assert.Equal([100, 400], WindowFinder.VisibleEdges(windows, EdgeAxis.Vertical, 60).ToArray());
        // Горизонтальные края на столбце 150: только второе окно, оба края видны.
        Assert.Equal([50, 350], WindowFinder.VisibleEdges(windows, EdgeAxis.Horizontal, 150).ToArray());
    }
}

using Screeny.Overlay;

namespace Screeny.Tests.Overlay;

public class CoordinateMapperTests
{
    [Fact]
    public void PixelToDip_DividesByScale()
    {
        var m = new CoordinateMapper(new PixelPoint(0, 0), dpiScale: 1.5);
        var p = m.PixelToDip(new PixelPoint(300, 150));
        Assert.Equal(200, p.X, 3); Assert.Equal(100, p.Y, 3);
    }

    [Fact]
    public void PixelToDip_Rect_DividesByScale()
    {
        var m = new CoordinateMapper(new PixelPoint(-1920, 0), dpiScale: 1.25);
        var r = m.PixelToDip(new PixelRect(125, 250, 500, 375));
        Assert.Equal(new System.Windows.Rect(100, 200, 400, 300), r);
    }

    [Fact]
    public void NonPositiveScale_FallsBackToOne()
    {
        Assert.Equal(1.0, new CoordinateMapper(new PixelPoint(0, 0), 0).DpiScale);
    }
}

using Screeny.Overlay;

namespace Screeny.Tests.Overlay;

public class EdgeDistancesTests
{
    private const int W = 240, H = 140;

    /// <summary>Светлый фон, тёмная «кнопка» 80×24 со столбцами 60…139 и строками 40…63.</summary>
    private static ILumaSource Button(int left = 60, int top = 40, int width = 80, int height = 24)
    {
        var px = new byte[W * H * 4];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                bool inside = x >= left && x < left + width && y >= top && y < top + height;
                byte v = inside ? (byte)40 : (byte)235;
                int i = (y * W + x) * 4;
                px[i] = px[i + 1] = px[i + 2] = v;
                px[i + 3] = 255;
            }
        return new BgraLuma(px, W, H, W * 4);
    }

    private static readonly PixelRect All = new(0, 0, W, H);

    [Fact]
    public void InsideButton_MeasuresToEachEdge()
    {
        var d = EdgeDistances.Measure(new EdgeSnapper(Button(), EdgeDistances.BandHalf), new PixelPoint(100, 52), All)!.Value;
        Assert.Equal((60, 140, 40, 64), (d.Left, d.Right, d.Top, d.Bottom));
        Assert.Equal((40, 39, 12, 11), (d.ToLeft, d.ToRight, d.ToTop, d.ToBottom));
        Assert.Equal(80, d.ToLeft + 1 + d.ToRight); // ширина кнопки
    }

    [Fact]
    public void OutsideButton_MeasuresGapToIt()
    {
        // Левее кнопки на той же высоте: справа до её левого края, слева край не найден — до границы области.
        var d = EdgeDistances.Measure(new EdgeSnapper(Button(), EdgeDistances.BandHalf), new PixelPoint(20, 52), All)!.Value;
        Assert.Equal(0, d.Left);
        Assert.Equal(60, d.Right);
        Assert.Equal(39, d.ToRight);
    }

    [Fact]
    public void WideBand_MissesShortButton_SoDistancesUseANarrowOne()
    {
        // Полоса прилипания рамки (±20 строк) шире кнопки в 24 px: её вертикальный край там не проходит согласие строк.
        var wide = new EdgeSnapper(Button());
        Assert.Null(wide.FindNext(EdgeAxis.Vertical, 100, 52, -1, 1));
        Assert.Equal(60, new EdgeSnapper(Button(), EdgeDistances.BandHalf).FindNext(EdgeAxis.Vertical, 100, 52, -1, 1));
    }

    [Fact]
    public void FlatImage_ReachesBounds()
    {
        var bounds = new PixelRect(30, 20, 150, 100);
        var d = EdgeDistances.Measure(new EdgeSnapper(Button(0, 0, 0, 0), EdgeDistances.BandHalf), new PixelPoint(50, 50), bounds)!.Value;
        Assert.Equal((30, 180, 20, 120), (d.Left, d.Right, d.Top, d.Bottom));
    }

    [Fact]
    public void EdgeOutsideBounds_IsIgnored()
    {
        // Кнопка шире области: её края за пределами, линии упираются в границы области.
        var bounds = new PixelRect(80, 30, 40, 50);
        var d = EdgeDistances.Measure(new EdgeSnapper(Button(), EdgeDistances.BandHalf), new PixelPoint(100, 52), bounds)!.Value;
        Assert.Equal((80, 120, 40, 64), (d.Left, d.Right, d.Top, d.Bottom));
    }

    [Fact]
    public void PointOutsideBounds_GivesNothing()
        => Assert.Null(EdgeDistances.Measure(new EdgeSnapper(Button()), new PixelPoint(5, 5), new PixelRect(10, 10, 50, 50)));

    [Fact]
    public void PointAtImageCorner_DoesNotLookPastIt()
    {
        var d = EdgeDistances.Measure(new EdgeSnapper(Button(), EdgeDistances.BandHalf), new PixelPoint(0, 0), All)!.Value;
        Assert.Equal((0, 0), (d.ToLeft, d.ToTop));
        Assert.True(d.ToRight >= 0 && d.ToBottom >= 0);
    }

    [Fact]
    public void FindNext_GoesOnlyOneWay()
    {
        var s = new EdgeSnapper(Button(), EdgeDistances.BandHalf);
        Assert.Equal(140, s.FindNext(EdgeAxis.Vertical, 101, 52, +1, W - 1));
        Assert.Equal(60, s.FindNext(EdgeAxis.Vertical, 100, 52, -1, 1));
        Assert.Null(s.FindNext(EdgeAxis.Vertical, 100, 52, -1, 61));   // предел ближе края
        Assert.Null(s.FindNext(EdgeAxis.Vertical, 100, 52, 0, 1));     // нет направления
        Assert.Null(s.FindNext(EdgeAxis.Vertical, 10, 52, -1, 20));    // начало уже за пределом
    }
}

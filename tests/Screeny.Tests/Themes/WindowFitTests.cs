using System.Windows;
using Screeny.Themes;

namespace Screeny.Tests.Themes;

/// <summary>
/// Windows per monitor they opened on: on Full HD next to 2K monitor settings must not go off-screen.
/// </summary>
public class WindowFitTests
{
    [Theory]
    [InlineData(1400, double.PositiveInfinity, 1360)] // 2K without natural cap: work area minus margin
    [InlineData(1400, 780, 780)]                      // settings on 2K: not tower
    [InlineData(1040, 780, 780)]                      // Full HD at 100%
    [InlineData(690, 780, 650)]                       // Full HD at 150%: work area smaller than natural cap
    [InlineData(380, 780, 360)]                       // very low monitor: not less than minimum…
    [InlineData(300, 780, 300)]                       // …unless monitor itself lower
    public void CapHeight_IsTheSmallerOfNaturalCapAndWorkAreaMinusMargin(double work, double natural, double expected)
        => Assert.Equal(expected, WindowFit.CapHeight(work, natural));

    [Fact]
    public void FitInto_CentersInsideTheWorkArea()
    {
        // Window opened centered on primary 2K monitor, but should go to Full HD right of it.
        var work = new Rect(2560, 0, 1920, 1040);
        var fitted = WindowFit.FitInto(new Rect(1000, -150, 560, 780), work, center: true);
        Assert.Equal(new Rect(2560 + (1920 - 560) / 2.0, (1040 - 780) / 2.0, 560, 780), fitted);
    }

    [Fact]
    public void FitInto_TooTallWindow_ShrinksToTheWorkArea()
    {
        var fitted = WindowFit.FitInto(new Rect(100, -100, 560, 1352), new Rect(0, 0, 1920, 1040), center: true);
        Assert.Equal(new Rect((1920 - 560) / 2.0, 0, 560, 1040), fitted);
    }

    [Theory]
    [InlineData(-50, -20, 0, 0)]        // beyond top-left edge — to edge
    [InlineData(1800, 900, 1360, 260)]  // beyond bottom-right — to fit fully
    [InlineData(300, 200, 300, 200)]    // inside — stays
    public void FitInto_WithoutCentering_MovesTheLeastNeeded(double x, double y, double ex, double ey)
        => Assert.Equal(new Rect(ex, ey, 560, 780), WindowFit.FitInto(new Rect(x, y, 560, 780), new Rect(0, 0, 1920, 1040), center: false));

    [Fact]
    public void FitInto_WorkAreaOnTheLeftOfPrimary_NegativeCoordinates()
        => Assert.Equal(new Rect(-1366 + 403, 0, 560, 728), WindowFit.FitInto(new Rect(0, 0, 560, 900), new Rect(-1366, 0, 1366, 728), center: true));

    [Theory]
    [InlineData(1.0, 1040)]
    [InlineData(1.25, 832)]
    [InlineData(1.5, 693.333)]
    public void ToDips_DividesByTheMonitorScale(double scale, double height)
    {
        var dips = WindowFit.ToDips(new Rect(1920, 0, 1920, 1040), scale);
        Assert.Equal(height, dips.Height, 3);
        Assert.Equal(1920 / scale, dips.X, 3);
    }

    [Fact]
    public void CapHeight_AtTheMonitorScale_KeepsAFullHd150WindowOnScreen()
    {
        // Full HD at 150%: work area 1040 px = 693 DIP; settings window ≤ 653 DIP = 980 px — with margin.
        double cap = WindowFit.CapHeight(WindowFit.ToDips(new Rect(0, 0, 1920, 1040), 1.5).Height, 780);
        Assert.True(cap * 1.5 <= 1040 - WindowFit.Margin, cap.ToString());
    }

    [Theory]
    [InlineData("0,0,1366,728", 0, 0, 1366, 728)]
    [InlineData(" 1920, 0, 1280, 680 ", 1920, 0, 1280, 680)]
    public void ParseWorkArea_ReadsDevOverride(string text, double x, double y, double w, double h)
        => Assert.Equal(new Rect(x, y, w, h), WindowFit.ParseWorkArea(text));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1,2,3")]
    [InlineData("0,0,0,700")]
    [InlineData("a,b,c,d")]
    public void ParseWorkArea_RejectsGarbage(string? text) => Assert.Null(WindowFit.ParseWorkArea(text));
}

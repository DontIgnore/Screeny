using System.Windows;
using Screeny.Overlay;

namespace Screeny.Tests.Overlay;

public class HudLayoutTests
{
    private static readonly Rect Work = new(0, 0, 1280, 800);
    private static readonly Size Bar = new(1040, 40);

    private static void AssertInside(Rect r, Rect work, string what)
        => Assert.True(r.Left >= work.Left && r.Right <= work.Right && r.Top >= work.Top && r.Bottom <= work.Bottom, $"{what} {r} outside {work}");

    [Fact]
    public void RoomBelow_ToolbarUnderSelection_FlyoutsOpenDownward()
    {
        var sel = new Rect(100, 100, 600, 300);
        var p = HudLayout.Place(sel, Work, Bar);
        Assert.Equal(sel.Bottom + HudLayout.Gap, p.Toolbar.Y, 3);
        Assert.Equal(100, p.Toolbar.X, 3);
        Assert.True(p.Below);
    }

    [Fact]
    public void NoRoomBelow_ToolbarAboveSelection()
    {
        var sel = new Rect(100, 300, 600, 470);
        var p = HudLayout.Place(sel, Work, Bar);
        Assert.Equal(sel.Top - HudLayout.Gap - Bar.Height, p.Toolbar.Y, 3);
        Assert.False(p.Below);
    }

    [Fact]
    public void SelectionFillsMonitor_ToolbarInsideAtBottom()
    {
        var p = HudLayout.Place(new Rect(0, 0, 1280, 800), Work, Bar);
        Assert.Equal(Work.Bottom - HudLayout.Gap - Bar.Height, p.Toolbar.Y, 3);
        Assert.False(p.Below);
    }

    [Theory]
    [InlineData(0, 0, 1280, 800)]
    [InlineData(0, 760, 1280, 40)]
    [InlineData(0, 0, 1280, 30)]
    [InlineData(1200, 400, 80, 80)]
    public void AnySelection_ToolbarStaysInsideWork(double x, double y, double w, double h)
    {
        var p = HudLayout.Place(new Rect(x, y, w, h), Work, Bar);
        AssertInside(new Rect(p.Toolbar, Bar), Work, "toolbar");
    }

    [Fact]
    public void ToolbarClampedHorizontally()
    {
        var p = HudLayout.Place(new Rect(1200, 100, 60, 60), Work, Bar);
        Assert.True(p.Toolbar.X + Bar.Width <= Work.Right - HudLayout.Gap + 0.001);
    }

    [Fact]
    public void SelectionAcrossOffsetMonitors_ToolbarStaysOnSecondMonitor()
    {
        var secondMonitorWork = new Rect(2560, 60, 1920, 1032);
        var selection = new Rect(2500, 100, 500, 400);
        var placement = HudLayout.Place(selection, secondMonitorWork, Bar);
        AssertInside(new Rect(placement.Toolbar, Bar), secondMonitorWork, "toolbar");
        Assert.True(placement.Toolbar.X >= secondMonitorWork.Left);
    }

    // ---------- PlaceFlyout ----------

    private static readonly Size Fly = new(220, 120);

    [Fact]
    public void Flyout_CenteredUnderAnchor_WhenPreferBelow()
    {
        var anchor = new Rect(500, 300, 32, 40);
        var f = HudLayout.PlaceFlyout(anchor, Fly, Work, preferBelow: true);
        Assert.True(f.Below);
        Assert.Equal(anchor.Bottom + HudLayout.FlyoutGap, f.TopLeft.Y, 3);
        Assert.Equal(anchor.Left + anchor.Width / 2, f.TopLeft.X + Fly.Width / 2, 3);
    }

    [Fact]
    public void Flyout_AboveAnchor_WhenNotPreferBelow()
    {
        var anchor = new Rect(500, 300, 32, 40);
        var f = HudLayout.PlaceFlyout(anchor, Fly, Work, preferBelow: false);
        Assert.False(f.Below);
        Assert.Equal(anchor.Top - HudLayout.FlyoutGap - Fly.Height, f.TopLeft.Y, 3);
    }

    [Theory]
    [InlineData(2)]      // у левого края
    [InlineData(1260)]   // у правого края
    public void Flyout_ClampedToWorkHorizontally(double anchorX)
    {
        var f = HudLayout.PlaceFlyout(new Rect(anchorX, 300, 18, 40), Fly, Work, preferBelow: true);
        AssertInside(new Rect(f.TopLeft, Fly), Work, "flyout");
        Assert.True(f.TopLeft.X >= Work.Left + HudLayout.Gap - 0.001 && f.TopLeft.X + Fly.Width <= Work.Right - HudLayout.Gap + 0.001);
    }

    [Fact]
    public void Flyout_FlipsAbove_WhenNoRoomBelow()
    {
        var anchor = new Rect(500, 720, 32, 40); // снизу до края 40 px
        var f = HudLayout.PlaceFlyout(anchor, Fly, Work, preferBelow: true);
        Assert.False(f.Below);
        Assert.Equal(anchor.Top - HudLayout.FlyoutGap - Fly.Height, f.TopLeft.Y, 3);
    }

    [Fact]
    public void Flyout_FlipsBelow_WhenNoRoomAbove()
    {
        var anchor = new Rect(500, 30, 32, 40);
        var f = HudLayout.PlaceFlyout(anchor, Fly, Work, preferBelow: false);
        Assert.True(f.Below);
    }

    [Fact]
    public void Flyout_NoRoomEitherSide_StaysInsideWork()
    {
        var work = new Rect(0, 0, 1280, 200);
        var f = HudLayout.PlaceFlyout(new Rect(500, 60, 32, 80), Fly, work, preferBelow: true);
        AssertInside(new Rect(f.TopLeft, Fly), work, "flyout");
    }

    [Fact]
    public void Flyout_AvoidsToolbar_FlipsToOtherSide()
    {
        // Фигура прямо над панелью: снизу flyout лёг бы на панель — встаёт над фигурой.
        var shape = new Rect(400, 500, 200, 100);
        var toolbar = new Rect(300, 606, 600, 40);
        var f = HudLayout.PlaceFlyout(shape, Fly, Work, preferBelow: true, avoid: toolbar);
        Assert.False(f.Below);
        Assert.False(HudLayout.Overlaps(new Rect(f.TopLeft, Fly), toolbar));
    }

    [Fact]
    public void Flyout_FromToolbarButton_NeverCoversToolbar_FullScreenSelection()
    {
        // Выделение на весь экран: панель внутри у нижнего края, flyout кнопки — над панелью, в пределах экрана.
        var p = HudLayout.Place(Work, Work, Bar);
        var bar = new Rect(p.Toolbar, Bar);
        foreach (double bx in new[] { bar.Left + 5, bar.Left + 400, bar.Right - 37 })
        {
            var anchor = new Rect(bx, bar.Top, 32, bar.Height);
            var f = HudLayout.PlaceFlyout(anchor, Fly, Work, p.Below, bar);
            var r = new Rect(f.TopLeft, Fly);
            AssertInside(r, Work, "flyout");
            Assert.False(HudLayout.Overlaps(r, bar), $"flyout {r} covers toolbar {bar}");
        }
    }

    [Fact]
    public void Flyout_ShapeNearTop_GoesBelowShape()
    {
        var shape = new Rect(300, 10, 200, 100);
        var f = HudLayout.PlaceFlyout(shape, Fly, Work, preferBelow: false, avoid: new Rect(0, 760, 1280, 40));
        Assert.True(f.Below);
        Assert.Equal(shape.Bottom + HudLayout.FlyoutGap, f.TopLeft.Y, 3);
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Overlay;

namespace Screeny.Tests.Overlay;

[Collection(WpfControls.Name)]
public class MagnifierControlTests
{
    private static void Sta(Action body)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { body(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error is not null) throw new Xunit.Sdk.XunitException(error.ToString());
    }

    [Fact]
    public void ShowColor_Off_HidesHexButKeepsCoordinates() => Sta(() =>
    {
        var magnifier = new MagnifierControl();
        var shot = BitmapSource.Create(4, 4, 96, 96, PixelFormats.Bgra32, null, new byte[4 * 4 * 4], 16);
        magnifier.Update(shot, new PixelPoint(1, 2));
        var hex = (TextBlock)magnifier.FindName("ColorText");
        var coords = (TextBlock)magnifier.FindName("Coords");
        Assert.True(magnifier.ShowColor); // default (editor: magnifier only in eyedropper mode)

        magnifier.ShowColor = false; // disabled eyedropper: HEX at magnifier not shown
        Assert.False(IsShown(hex));
        Assert.True(IsShown(coords));
        Assert.Equal("1, 2", coords.Text);

        magnifier.ShowColor = true;
        Assert.True(IsShown(hex));
    });

    /// <summary>Without window IsVisible always false: check Visibility through ancestor chain.</summary>
    private static bool IsShown(FrameworkElement element)
    {
        for (DependencyObject? e = element; e is not null; e = VisualTreeHelper.GetParent(e) ?? LogicalTreeHelper.GetParent(e))
            if (e is UIElement { Visibility: not Visibility.Visible }) return false;
        return true;
    }
}

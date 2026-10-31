using System.Windows;
using System.Windows.Media;
using Screeny.Capture;

namespace Screeny.Tests.Capture;

public class ScreenCaptureTests
{
    [Fact]
    public void CaptureVirtualScreen_ReturnsFrozenOpaqueBgra32()
    {
        var shot = ScreenCapture.CaptureVirtualScreen();
        var bmp = shot.Bitmap;
        Assert.True(bmp.IsFrozen);
        Assert.Equal(PixelFormats.Bgra32, bmp.Format);
        Assert.Equal(shot.VirtualRect.Width, bmp.PixelWidth);
        Assert.Equal(shot.VirtualRect.Height, bmp.PixelHeight);

        // Alpha set across entire buffer: check first, last row and middle.
        int w = bmp.PixelWidth, h = bmp.PixelHeight;
        var row = new byte[w * 4];
        foreach (int y in new[] { 0, h / 2, h - 1 })
        {
            bmp.CopyPixels(new Int32Rect(0, y, w, 1), row, row.Length, 0);
            for (int i = 3; i < row.Length; i += 4) Assert.Equal(255, row[i]);
        }
    }
}

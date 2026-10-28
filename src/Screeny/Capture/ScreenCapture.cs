using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Native;
using Screeny.Overlay;

namespace Screeny.Capture;

public static class ScreenCapture
{
    public static Screenshot CaptureVirtualScreen()
    {
        int x = User32.GetSystemMetrics(User32.SM_XVIRTUALSCREEN), y = User32.GetSystemMetrics(User32.SM_YVIRTUALSCREEN);
        int w = User32.GetSystemMetrics(User32.SM_CXVIRTUALSCREEN), h = User32.GetSystemMetrics(User32.SM_CYVIRTUALSCREEN);
        if (w <= 0 || h <= 0) throw new InvalidOperationException($"Invalid virtual screen size: {w}×{h}");

        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, hBitmap = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            screenDc = User32.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) throw new InvalidOperationException("GetDC(screen) returned NULL");
            memDc = Gdi32.CreateCompatibleDC(screenDc);
            if (memDc == IntPtr.Zero) throw new InvalidOperationException("CreateCompatibleDC returned NULL");
            hBitmap = Gdi32.CreateCompatibleBitmap(screenDc, w, h);
            if (hBitmap == IntPtr.Zero) throw new InvalidOperationException($"CreateCompatibleBitmap {w}×{h} returned NULL (out of GDI memory?)");
            old = Gdi32.SelectObject(memDc, hBitmap);

            if (!Gdi32.BitBlt(memDc, 0, 0, w, h, screenDc, x, y, Gdi32.SRCCOPY | Gdi32.CAPTUREBLT))
                throw new InvalidOperationException("BitBlt failed");
            Gdi32.SelectObject(memDc, old); // GetDIBits требует, чтобы битмап не был выбран в DC
            old = IntPtr.Zero;

            // DPI 96: дальше везде работаем в пикселях и масштабируем сами.
            // GetDIBits пишет сразу в back buffer — без промежуточного byte[] и копии в BitmapSource.Create.
            var bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
            bitmap.Lock();
            try
            {
                if (bitmap.BackBufferStride != w * 4)
                    throw new InvalidOperationException($"Unexpected back buffer stride {bitmap.BackBufferStride} for width {w}");
                var header = new Gdi32.BITMAPINFOHEADER
                {
                    biSize = (uint)Marshal.SizeOf<Gdi32.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, // top-down
                    biPlanes = 1, biBitCount = 32, biCompression = Gdi32.BI_RGB,
                };
                if (Gdi32.GetDIBits(memDc, hBitmap, 0, (uint)h, bitmap.BackBuffer, ref header, Gdi32.DIB_RGB_COLORS) != h)
                    throw new InvalidOperationException("GetDIBits failed");
                // GDI оставляет альфу нулевой — без этого PNG получился бы прозрачным.
                SetOpaque(bitmap.BackBuffer, (long)w * h);
                bitmap.AddDirtyRect(new Int32Rect(0, 0, w, h));
            }
            finally { bitmap.Unlock(); }
            bitmap.Freeze();
            return new Screenshot(bitmap, new PixelRect(x, y, w, h));
        }
        finally
        {
            if (old != IntPtr.Zero) Gdi32.SelectObject(memDc, old);
            if (hBitmap != IntPtr.Zero) Gdi32.DeleteObject(hBitmap);
            if (memDc != IntPtr.Zero) Gdi32.DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) User32.ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static unsafe void SetOpaque(IntPtr buffer, long pixelCount)
    {
        uint* p = (uint*)buffer;
        for (long i = 0; i < pixelCount; i++) p[i] |= 0xFF000000u;
    }
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Screeny.Logging;

namespace Screeny.Export;

public static class ClipboardService
{
    /// <summary>CLIPBRD_E_CANT_OPEN: clipboard busy by another process.</summary>
    public const int ClipboardCantOpen = unchecked((int)0x800401D0);

    /// <summary>PNG bytes of screenshot: encoded once and reused (clipboard, history).</summary>
    public static byte[] EncodePng(BitmapSource image)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Puts PNG (with alpha) and DIB/Bitmap. false — clipboard busy (after WPF retries), error logged.</summary>
    public static bool CopyImage(BitmapSource image) => CopyImage(image, EncodePng(image));

    /// <param name="png">Already encoded <see cref="EncodePng"/> PNG of same image.</param>
    public static bool CopyImage(BitmapSource image, byte[] png)
    {
        var data = new DataObject();
        data.SetData("PNG", new MemoryStream(png, writable: false), autoConvert: false);
        data.SetImage(image); // CF_DIB / CF_BITMAP for Paint, Office etc.
        return TrySet(data);
    }

    /// <summary>false — clipboard busy (after WPF retries), error logged.</summary>
    public static bool CopyText(string text) => TrySet(text);

    /// <summary>
    /// Clipboard.SetDataObject(copy: true) itself retries OleSetClipboard/OleFlushClipboard (10 × 100ms),
    /// so no own retries with Sleep on UI thread. Only “busy” is swallowed; other errors propagate.
    /// </summary>
    private static bool TrySet(object data)
    {
        try
        {
            Clipboard.SetDataObject(data, copy: true);
            return true;
        }
        catch (COMException ex) when (ex.HResult == ClipboardCantOpen)
        {
            Log.Error("Clipboard is busy (CLIPBRD_E_CANT_OPEN)", ex);
            return false;
        }
    }
}

using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Screeny.Logging;

namespace Screeny.Export;

public static class ClipboardService
{
    /// <summary>CLIPBRD_E_CANT_OPEN: буфер обмена занят другим процессом.</summary>
    public const int ClipboardCantOpen = unchecked((int)0x800401D0);

    /// <summary>PNG-байты снимка: кодируются один раз и переиспользуются (буфер обмена, история).</summary>
    public static byte[] EncodePng(BitmapSource image)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// <summary>Кладёт PNG (с альфой) и DIB/Bitmap. false — буфер занят (после повторов WPF), ошибка записана в лог.</summary>
    public static bool CopyImage(BitmapSource image) => CopyImage(image, EncodePng(image));

    /// <param name="png">Уже закодированный <see cref="EncodePng"/> PNG того же изображения.</param>
    public static bool CopyImage(BitmapSource image, byte[] png)
    {
        var data = new DataObject();
        data.SetData("PNG", new MemoryStream(png, writable: false), autoConvert: false);
        data.SetImage(image); // CF_DIB / CF_BITMAP для Paint, Office и т.п.
        return TrySet(data);
    }

    /// <summary>false — буфер занят (после повторов WPF), ошибка записана в лог.</summary>
    public static bool CopyText(string text) => TrySet(text);

    /// <summary>
    /// Clipboard.SetDataObject(copy: true) сам повторяет OleSetClipboard/OleFlushClipboard (10 × 100 мс),
    /// поэтому своих повторов со Sleep на UI-потоке нет. Глушим только «буфер занят»; прочие ошибки — наверх.
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

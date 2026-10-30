using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;
using Screeny.Logging;

namespace Screeny.Export;

public enum ClipboardImageStatus { Image, NoImage, Busy }

/// <summary>Картинка из буфера обмена (заморожена) или причина, почему её нет.</summary>
public sealed record ClipboardImageResult(ClipboardImageStatus Status, BitmapSource? Image = null)
{
    public static readonly ClipboardImageResult None = new(ClipboardImageStatus.NoImage);
    public static readonly ClipboardImageResult Busy = new(ClipboardImageStatus.Busy);
}

/// <summary>Чтение буфера обмена; подменяется в тестах, чтобы не трогать настоящий буфер.</summary>
public interface IClipboardSource
{
    bool ContainsData(string format);
    object? GetData(string format);
    /// <summary>Пути файлов, скопированных в проводнике; null — их нет.</summary>
    IReadOnlyList<string>? GetFileDropList();
    /// <summary>CF_DIB / CF_BITMAP как картинка; null — их нет.</summary>
    BitmapSource? GetBitmap();
}

/// <summary>Настоящий буфер обмена Windows (вызывать из UI-потока).</summary>
public sealed class SystemClipboardSource : IClipboardSource
{
    public bool ContainsData(string format) => Clipboard.ContainsData(format);
    public object? GetData(string format) => Clipboard.GetData(format);

    public IReadOnlyList<string>? GetFileDropList()
    {
        if (!Clipboard.ContainsFileDropList()) return null;
        StringCollection files = Clipboard.GetFileDropList();
        return files.Cast<string>().ToList();
    }

    public BitmapSource? GetBitmap() => Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
}

/// <summary>
/// Картинка для «Закрепить из буфера обмена». Порядок: PNG (с прозрачностью — его кладут браузеры и сама Screeny),
/// затем один скопированный файл картинки, затем DIB/Bitmap. Буфер не меняется.
/// </summary>
public static class ClipboardImageReader
{
    /// <summary>Расширения файлов, которые декодирует WPF без сторонних кодеков.</summary>
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".ico"];

    public static ClipboardImageResult Read(IClipboardSource clipboard)
    {
        try
        {
            if (clipboard.ContainsData("PNG") && FromPng(clipboard.GetData("PNG")) is { } png) return new(ClipboardImageStatus.Image, png);
            if (clipboard.GetFileDropList() is { Count: 1 } files && IsImageFile(files[0]) && FromFile(files[0]) is { } file)
                return new(ClipboardImageStatus.Image, file);
            if (clipboard.GetBitmap() is { } bitmap)
            {
                if (!bitmap.IsFrozen) bitmap = (BitmapSource)bitmap.GetAsFrozen();
                return new(ClipboardImageStatus.Image, bitmap);
            }
            return ClipboardImageResult.None;
        }
        catch (COMException ex) when (ex.HResult == ClipboardService.ClipboardCantOpen)
        {
            Log.Error("Clipboard is busy (CLIPBRD_E_CANT_OPEN) while reading an image", ex);
            return ClipboardImageResult.Busy;
        }
        catch (Exception ex) when (ex is COMException or ExternalException or OutOfMemoryException)
        {
            Log.Error("Clipboard image read failed", ex);
            return ClipboardImageResult.None;
        }
    }

    public static bool IsImageFile(string path)
        => ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static BitmapSource? FromPng(object? data)
    {
        byte[]? bytes = data switch
        {
            MemoryStream ms => ms.ToArray(),
            Stream s => ReadAll(s),
            byte[] b => b,
            _ => null,
        };
        return bytes is { Length: > 0 } ? Decode(() => new MemoryStream(bytes, writable: false), "clipboard PNG") : null;
    }

    private static byte[] ReadAll(Stream s)
    {
        using var copy = new MemoryStream();
        s.CopyTo(copy);
        return copy.ToArray();
    }

    private static BitmapSource? FromFile(string path)
        => Decode(() => new MemoryStream(File.ReadAllBytes(path), writable: false), "clipboard file");

    /// <summary>Декодирует целиком в память (файл не остаётся открытым); битые данные — null с записью в журнал.</summary>
    private static BitmapSource? Decode(Func<Stream> open, string what)
    {
        try
        {
            using var stream = open();
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp.PixelWidth > 0 && bmp.PixelHeight > 0 ? bmp : null;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException
                                       or FileFormatException or ArgumentException or InvalidOperationException)
        {
            Log.Error($"Clipboard image decode failed ({what})", ex);
            return null;
        }
    }
}

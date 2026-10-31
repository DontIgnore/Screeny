using System.Windows.Media.Imaging;
using Screeny.History;

namespace Screeny.Export;

public sealed record ImageSaveResult(byte[] Png, bool InHistory, Exception? HistoryError, Exception? SaveError);

/// <summary>
/// Encodes screenshots and sequentially performs file operations outside UI thread. Without storage (History feature
/// disabled) screenshot is only encoded and, if path selected, saved to file: InHistory = false without error.
/// </summary>
public sealed class ImageSaveWorker
{
    public BackgroundQueue Queue { get; } = new();

    public Task<ImageSaveResult> SaveAsync(HistoryStore? store, BitmapSource image, string? json,
        BitmapSource? original, byte[]? originalPng, string? savePath, DateTime timestamp)
    {
        if (!image.IsFrozen || (original is not null && !original.IsFrozen))
            throw new ArgumentException("Images for background saving must be frozen.");
        return Queue.Enqueue(() => Save(store, image, json, original, originalPng, savePath, timestamp));
    }

    private static ImageSaveResult Save(HistoryStore? store, BitmapSource image, string? json,
        BitmapSource? original, byte[]? originalPng, string? savePath, DateTime timestamp)
    {
        var png = ClipboardService.EncodePng(image);
        Exception? historyError = null, saveError = null;
        if (store is not null)
        {
            try
            {
                var originalBytes = original is null ? originalPng : ClipboardService.EncodePng(original);
                var sidecar = json is null && originalBytes is null ? null : new HistorySidecar(json, originalBytes);
                store.Save(png, sidecar, timestamp);
            }
            catch (Exception ex) { historyError = ex; }
        }
        if (savePath is not null)
        {
            try { FileSaver.Save(image, savePath); }
            catch (Exception ex) { saveError = ex; }
        }
        return new ImageSaveResult(png, store is not null && historyError is null, historyError, saveError);
    }
}

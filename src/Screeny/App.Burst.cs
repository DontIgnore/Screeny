using Screeny.Capture;
using Screeny.Features;
using Screeny.Hotkeys;
using Screeny.Logging;
using Screeny.Overlay;

namespace Screeny;

// Серия снимков по удержанию клавиши снимка. Короткое нажатие — обычный снимок: кадр снят по WM_HOTKEY, как раньше, а
// оверлей показывается, как только видно, что клавишу отпустили (опрос GetAsyncKeyState, BurstRecorder). Держат дольше
// 250 мс — около 10 кадров в секунду до отпускания, затем оверлей с лентой кадров. Пока держат, оверлей не показывается:
// он забрал бы фокус и закрыл бы меню и подсказки, ради которых серию снимают. Серии нет у хука Win+Shift+S (его S
// проглатывается и не читается), у клика в трее и сигнала второго экземпляра, в тестовом экземпляре (там --burst-test).
public partial class App
{
    private void OnHotkeyPressed(HotkeySpec? spec)
    {
        int? hold = spec is { } s && Settings.IsFeatureOn(FeatureCatalog.Burst) ? s.VirtualKey : null;
        StartCapture(hold);
    }

    /// <summary>
    /// Решение по удержанию пришло (UI-поток): серия — кадры в оверлей (сначала последний: обычно ради него и держали),
    /// затем показ. Оверлей уже закрыт (сменились мониторы) или идёт выход — не показываем. Возвращает, была ли серия.
    /// </summary>
    private bool OpenAfterHold(OverlayWindow overlay, BurstResult result, bool closed)
    {
        if (result.IsBurst)
            Log.Error($"Burst: {result.Frames.Count} frame(s) kept, {result.Duplicates} duplicate(s) dropped, held {result.Held.TotalMilliseconds:0} ms, " +
                      $"{result.CaptureMs:0} ms per frame{(result.HitLimit ? ", frame or memory limit reached" : "")}{(result.Stopped is { } why ? ", stopped: " + why : "")}");
        if (closed) return result.IsBurst;
        if (IsShuttingDown) { overlay.Close(); return result.IsBurst; }
        if (result.Frames.Count > 1) overlay.SetBurst(result.Frames, result.Frames.Count - 1);
        overlay.Show();
        return result.IsBurst;
    }

    /// <summary>
    /// Оверлей серии закрыт: кадры (до 600 МБ в памяти WIC) больше не держит никто, кроме сборщика мусора. Собираем сразу,
    /// а не когда-нибудь: иначе серия висела бы в памяти процесса до следующей сборки.
    /// </summary>
    private void ReleaseBurstMemory() => Dispatcher.BeginInvoke(() =>
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        long before = process.PrivateMemorySize64;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        process.Refresh();
        Log.Error($"Burst closed: private memory {before / (1024 * 1024)} MB -> {process.PrivateMemorySize64 / (1024 * 1024)} MB");
    }, System.Windows.Threading.DispatcherPriority.Background);

    /// <summary>
    /// <c>--dev --burst-test &lt;папка&gt;</c>: PNG из папки по имени файла как кадры серии. Кадры другого размера пропускаются,
    /// одинаковые подряд и сверх лимита отбрасываются тем же <see cref="BurstFrameBuffer"/>, что и в настоящей серии.
    /// </summary>
    private static (Screenshot Shot, List<BurstFrame> Frames) LoadDevBurst(string folder)
    {
        var files = Directory.GetFiles(folder, "*.png").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
        var buffer = new BurstFrameBuffer();
        var frames = new List<BurstFrame>();
        Screenshot? first = null;
        int otherSize = 0;
        foreach (var file in files)
        {
            var shot = DevShot.Load(file);
            if (first is null) first = shot;
            else if (shot.Bitmap.PixelWidth != first.Bitmap.PixelWidth || shot.Bitmap.PixelHeight != first.Bitmap.PixelHeight) { otherSize++; continue; }
            int w = shot.Bitmap.PixelWidth, h = shot.Bitmap.PixelHeight;
            var pixels = new byte[w * 4 * h];
            shot.Bitmap.CopyPixels(pixels, w * 4, 0);
            var offer = buffer.Offer(pixels);
            if (offer == BurstOffer.Full) break;
            if (offer == BurstOffer.Added) frames.Add(new BurstFrame(shot.Bitmap, [], BurstRecorder.Thumbnail(pixels, w, h)));
        }
        buffer.ReleaseLast();
        if (first is null) throw new InvalidOperationException($"No PNG frames in {folder}");
        Log.Error($"Dev burst: {files.Count} file(s), {frames.Count} frame(s) kept, {buffer.Duplicates} duplicate(s), {otherSize} of another size" +
                  (buffer.HitLimit ? ", limit reached" : ""));
        return (first, frames);
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Native;
using Screeny.Overlay;

namespace Screeny.Capture;

/// <summary>Кадр серии: снимок всего виртуального экрана (геометрия первого кадра), окна на нём и миниатюра для ленты.</summary>
public sealed record BurstFrame(BitmapSource Bitmap, IReadOnlyList<WindowInfo> Windows, BitmapSource Thumbnail);

/// <summary>Итог удержания клавиши снимка.</summary>
/// <param name="IsBurst">Удержание признано серией; false — короткое нажатие, оверлей открывается с первым кадром.</param>
/// <param name="Frames">Кадры серии, первый — снимок по нажатию; при коротком нажатии пусто.</param>
/// <param name="Held">Сколько прошло от нажатия до решения (отпускания или конца серии).</param>
/// <param name="Duplicates">Кадров отброшено как одинаковые с предыдущим.</param>
/// <param name="HitLimit">Серия упёрлась в лимит кадров или памяти.</param>
/// <param name="CaptureMs">Среднее время снятия одного кадра серии (BitBlt + копия), мс; 0 — кадров не снимали.</param>
/// <param name="Stopped">Почему серию остановили раньше отпускания (смена мониторов) или null.</param>
public sealed record BurstResult(bool IsBurst, IReadOnlyList<BurstFrame> Frames, TimeSpan Held, int Duplicates = 0, bool HitLimit = false,
    double CaptureMs = 0, string? Stopped = null);

/// <summary>
/// Серия по удержанию клавиши снимка, на своём фоновом потоке. <c>RegisterHotKey</c> не сообщает об отпускании, поэтому
/// основная клавиша сочетания опрашивается <c>GetAsyncKeyState</c> каждые <see cref="PollMs"/> мс, а решение принимает
/// <see cref="BurstTimeline"/>. Кадры снимаются тем же GDI-путём, что и обычный снимок (BitBlt с CAPTUREBLT, чтобы
/// попадали и всплывающие подсказки), с одним DC и битмапом на всю серию, и сразу становятся замороженными
/// <see cref="BitmapSource"/>: память серии не растёт после отпускания. Одинаковые подряд кадры и всё сверх лимита
/// отбрасывает <see cref="BurstFrameBuffer"/>. Оверлей во время серии не показывается: он забрал бы фокус и закрыл
/// меню и подсказки, ради которых серию снимают.
/// </summary>
public static class BurstRecorder
{
    /// <summary>Интервал опроса клавиши; на деле не меньше шага системного таймера (обычно до 15,6 мс).</summary>
    public const int PollMs = 5;
    /// <summary>Миниатюра в ленте: не шире и не выше этих размеров, пиксели.</summary>
    public const int ThumbWidth = 192, ThumbHeight = 108;

    /// <summary>
    /// Запускает опрос клавиши <paramref name="virtualKey"/> и, если её держат, серию. <paramref name="done"/>
    /// вызывается один раз на фоновом потоке — перейти в UI-поток должен вызывающий.
    /// </summary>
    /// <param name="clock">Часы от нажатия (WM_HOTKEY): порог удержания считается от него, а не от старта потока.</param>
    /// <param name="exclude">Свои окна, которые не нужны в списке окон кадров (собраны в UI-потоке заранее).</param>
    public static void Start(int virtualKey, Screenshot first, IReadOnlyList<WindowInfo> firstWindows, ISet<IntPtr> exclude, Stopwatch clock,
        Action<BurstResult> done)
    {
        var thread = new Thread(() =>
        {
            BurstResult result;
            try { result = Run(virtualKey, first, firstWindows, exclude, clock); }
            catch (Exception ex)
            {
                Screeny.Logging.Log.Error("Burst capture failed; opening the first frame", ex);
                result = new BurstResult(false, [], clock.Elapsed);
            }
            done(result);
        }) { IsBackground = true, Name = "Screeny.Burst" };
        thread.Start();
    }

    private static BurstResult Run(int virtualKey, Screenshot first, IReadOnlyList<WindowInfo> firstWindows, ISet<IntPtr> exclude, Stopwatch clock)
    {
        var timeline = new BurstTimeline();
        var buffer = new BurstFrameBuffer();
        var frames = new List<BurstFrame>();
        GdiGrabber? grabber = null;
        byte[]? pixels = null;
        string? stopped = null;
        double captureMs = 0;
        int captures = 0;
        try
        {
            while (true)
            {
                bool down = (User32.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
                switch (timeline.Step(clock.Elapsed, down, full: buffer.HitLimit || stopped is not null))
                {
                    case BurstStep.OpenTap:
                        return new BurstResult(false, [], clock.Elapsed);
                    case BurstStep.OpenBurst:
                        return new BurstResult(true, frames, clock.Elapsed, buffer.Duplicates, buffer.HitLimit, captures > 0 ? captureMs / captures : 0, stopped);
                    case BurstStep.Capture:
                        if (grabber is null)
                        {
                            // Удержание признано: первый кадр — уже сделанный по нажатию снимок.
                            var bitmap = first.Bitmap;
                            pixels = new byte[bitmap.PixelWidth * 4 * bitmap.PixelHeight];
                            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                            buffer.Offer(pixels);
                            frames.Add(new BurstFrame(bitmap, firstWindows, Thumbnail(pixels, bitmap.PixelWidth, bitmap.PixelHeight)));
                            grabber = new GdiGrabber(first.VirtualRect);
                        }
                        var started = Stopwatch.GetTimestamp();
                        if (!grabber.TryGrab(pixels!)) { stopped = "display layout changed"; break; }
                        captureMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                        captures++;
                        if (buffer.Offer(pixels) == BurstOffer.Added)
                        {
                            int w = first.VirtualRect.Width, h = first.VirtualRect.Height;
                            var image = BitmapSource.Create(w, h, 96, 96, PixelFormats.Bgra32, null, pixels, w * 4);
                            image.Freeze();
                            frames.Add(new BurstFrame(image, WindowFinder.Snapshot(first.VirtualRect, exclude), Thumbnail(pixels!, w, h)));
                        }
                        break;
                    default:
                        Thread.Sleep(PollMs);
                        break;
                }
            }
        }
        finally
        {
            grabber?.Dispose();
            buffer.ReleaseLast();
        }
    }

    /// <summary>Уменьшенная копия кадра (ближайший пиксель): для ленты этого достаточно, и это быстро.</summary>
    public static BitmapSource Thumbnail(byte[] bgra, int width, int height)
    {
        double k = Math.Min((double)ThumbWidth / width, (double)ThumbHeight / height);
        int tw = Math.Max(1, (int)Math.Round(width * k)), th = Math.Max(1, (int)Math.Round(height * k));
        var small = new byte[tw * th * 4];
        for (int y = 0; y < th; y++)
        {
            int sy = Math.Min(height - 1, (int)((y + 0.5) / k));
            for (int x = 0; x < tw; x++)
            {
                int sx = Math.Min(width - 1, (int)((x + 0.5) / k));
                Buffer.BlockCopy(bgra, (sy * width + sx) * 4, small, (y * tw + x) * 4, 4);
            }
        }
        var thumb = BitmapSource.Create(tw, th, 96, 96, PixelFormats.Bgra32, null, small, tw * 4);
        thumb.Freeze();
        return thumb;
    }

    /// <summary>
    /// GDI-захват прямоугольника виртуального экрана в готовый буфер Bgra32, с одним DC и битмапом на всю серию.
    /// Все кадры — в геометрии первого: изменилась раскладка мониторов — <see cref="TryGrab"/> возвращает false.
    /// </summary>
    private sealed class GdiGrabber : IDisposable
    {
        private readonly PixelRect _rect;
        private readonly IntPtr _screenDc, _memDc, _bitmap, _old;

        public GdiGrabber(PixelRect rect)
        {
            _rect = rect;
            _screenDc = User32.GetDC(IntPtr.Zero);
            if (_screenDc == IntPtr.Zero) throw new InvalidOperationException("GetDC(screen) returned NULL");
            _memDc = Gdi32.CreateCompatibleDC(_screenDc);
            _bitmap = Gdi32.CreateCompatibleBitmap(_screenDc, rect.Width, rect.Height);
            if (_memDc == IntPtr.Zero || _bitmap == IntPtr.Zero) { Dispose(); throw new InvalidOperationException("GDI objects for burst capture were not created"); }
            _old = Gdi32.SelectObject(_memDc, _bitmap);
        }

        public bool TryGrab(byte[] target)
        {
            int x = User32.GetSystemMetrics(User32.SM_XVIRTUALSCREEN), y = User32.GetSystemMetrics(User32.SM_YVIRTUALSCREEN);
            int w = User32.GetSystemMetrics(User32.SM_CXVIRTUALSCREEN), h = User32.GetSystemMetrics(User32.SM_CYVIRTUALSCREEN);
            if (new PixelRect(x, y, w, h) != _rect || target.Length != _rect.Width * 4 * _rect.Height) return false;
            if (!Gdi32.BitBlt(_memDc, 0, 0, w, h, _screenDc, x, y, Gdi32.SRCCOPY | Gdi32.CAPTUREBLT)) return false;
            var header = new Gdi32.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<Gdi32.BITMAPINFOHEADER>(), biWidth = w, biHeight = -h, // сверху вниз
                biPlanes = 1, biBitCount = 32, biCompression = Gdi32.BI_RGB,
            };
            // GetDIBits требует, чтобы битмап не был выбран в DC: на время чтения возвращаем прежний.
            Gdi32.SelectObject(_memDc, _old);
            var handle = GCHandle.Alloc(target, GCHandleType.Pinned);
            try
            {
                if (Gdi32.GetDIBits(_memDc, _bitmap, 0, (uint)h, handle.AddrOfPinnedObject(), ref header, Gdi32.DIB_RGB_COLORS) != h) return false;
            }
            finally
            {
                handle.Free();
                Gdi32.SelectObject(_memDc, _bitmap);
            }
            // GDI оставляет альфу нулевой, как и в обычном снимке.
            var px = MemoryMarshal.Cast<byte, uint>(target.AsSpan());
            for (int i = 0; i < px.Length; i++) px[i] |= 0xFF000000u;
            return true;
        }

        public void Dispose()
        {
            if (_old != IntPtr.Zero) Gdi32.SelectObject(_memDc, _old);
            if (_bitmap != IntPtr.Zero) Gdi32.DeleteObject(_bitmap);
            if (_memDc != IntPtr.Zero) Gdi32.DeleteDC(_memDc);
            if (_screenDc != IntPtr.Zero) User32.ReleaseDC(IntPtr.Zero, _screenDc);
        }
    }
}

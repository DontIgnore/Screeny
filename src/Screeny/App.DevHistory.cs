using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Export;
using Screeny.History;
using Screeny.Ocr;

namespace Screeny;

/// <summary>
/// <c>--dev --history --scenario search</c>: проверка поиска по тексту без настоящих снимков. Если синтетических снимков в тестовой истории ещё нет,
/// в неё через обычный путь сохранения кладутся синтетические снимки с английским и русским текстом (счёт, ошибка,
/// таблица…). Когда текст распознан, окно истории рисуется в PNG (<c>%LocalAppData%\Screeny\Dev\history-search-*.png</c>)
/// для нескольких запросов, а время распознавания одного снимка пишется в <c>history-search-timing.txt</c> рядом.
/// </summary>
public partial class App
{
    private static readonly (string Title, string[] Lines)[] DevSearchShots =
    [
        ("INVOICE #1042", ["Acme Tools GmbH, Berlin", "Date: 12 October 2026", "Widget, large    3 x 49.00", "Shipping    12.50", "Total due: 159.50 EUR"]),
        ("Счёт на оплату № 17", ["ООО «Ромашка», Москва", "Дата: 14 октября 2026 г.", "Услуги по настройке    1 шт.", "Итого к оплате: 4 500,00 ₽", "Без НДС"]),
        ("Error", ["Windows cannot access the specified device, path, or file.", "You may not have the appropriate permissions.", "Error code: 0x80070005 (Access is denied)"]),
        ("Ошибка подключения", ["Не удалось подключиться к серверу базы данных.", "Проверьте пароль и повторите попытку.", "Код ошибки: 1045"]),
        ("Build failed", ["src/Program.cs(42,17): error CS0103:", "The name 'conifg' does not exist in the current context", "1 error, 0 warnings"]),
        ("Q3 report", ["Region    Revenue    Growth", "North    1.2M    +8%", "South    0.9M    -2%", "Отчёт за третий квартал"]),
    ];

    private async void RunDevSearchScenario()
    {
        try
        {
            if (History is not { } store) return;
            var devFolder = Path.GetDirectoryName(Screeny.Settings.SettingsStore.DevHistoryFolder())!; // %LocalAppData%\Screeny\Dev
            // Список сохранённых синтетических снимков: при следующем запуске они не добавляются снова, пока все на месте.
            var seeded = Path.Combine(devFolder, "history-search-seeded.txt");
            if (!File.Exists(seeded) || !File.ReadAllLines(seeded).All(File.Exists))
            {
                var saved = new List<string>();
                foreach (var (title, lines) in DevSearchShots)
                    if ((await SaveImageAsync(RenderDevShot(title, lines), null, null, null, null)).HistoryPath is { } p) saved.Add(p);
                File.WriteAllLines(seeded, saved);
            }

            // Ждём, пока индексатор распознает всё (не дольше двух минут).
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromMinutes(2))
            {
                var items = await _imageSaver.Queue.Enqueue(store.List);
                if (items.All(i => i.TextPath is not null)) break;
                await Task.Delay(500);
            }
            await WriteDevOcrTimingAsync(Path.Combine(devFolder, "history-search-timing.txt"));
            ShowHistory();
            if (_historyWindow is not { } window) return;
            await Task.Delay(1500); // тексты и миниатюры загружаются в фоне
            foreach (var (file, query) in new[] { ("all", ""), ("invoice", "invoice total"), ("schet", "счет оплату"), ("romashka", "ромашка МОСКВА"), ("error", "error"), ("none", "zebra") })
            {
                await window.DevSearch(query);
                await Task.Delay(300);
                window.DevRenderTo(Path.Combine(devFolder, $"history-search-{file}.png"));
            }
            await window.DevSearch("");
        }
        catch (Exception ex) { Logging.Log.Error("Dev search scenario failed", ex); }
    }

    /// <summary>Подставной буфер обмена для <c>--scenario pin-clipboard</c>: настоящий буфер не читается и не меняется.</summary>
    private sealed class DevClipboard(byte[]? png) : IClipboardSource
    {
        public bool ContainsData(string format) => png is not null && format == "PNG";
        public object? GetData(string format) => png is not null && format == "PNG" ? new MemoryStream(png, writable: false) : null;
        public IReadOnlyList<string>? GetFileDropList() => null;
        public BitmapSource? GetBitmap() => null;
    }

    /// <summary>
    /// <c>--dev --history --scenario pin-clipboard</c>: «Закрепить из буфера обмена» с подставным буфером (синтетический PNG,
    /// затем пустой буфер — только уведомление) и «Закрепить» первой карточки отфильтрованной истории. Закреплённые окна
    /// рисуются в <c>pin-clipboard.png</c> и <c>pin-history.png</c>, итог — в <c>pin-scenario.txt</c> (папка Dev).
    /// </summary>
    private async void RunDevPinScenario()
    {
        try
        {
            var devFolder = Path.GetDirectoryName(Screeny.Settings.SettingsStore.DevHistoryFolder())!;
            var report = new StringBuilder();
            var png = ClipboardService.EncodePng(RenderDevShot("Pinned from clipboard", ["A synthetic picture, not the real clipboard.", "Закреплено из буфера обмена."], 560, 220));

            int before = Pins.Count;
            PinFromClipboard(new DevClipboard(png));
            report.AppendLine(CultureInfo.InvariantCulture, $"clipboard with PNG: pins {before} -> {Pins.Count}");
            await Task.Delay(800);
            if (Pins.Count > before) { DevRender(Pins[^1], Path.Combine(devFolder, "pin-clipboard.png")); Pins[^1].Close(); }

            before = Pins.Count;
            PinFromClipboard(new DevClipboard(null));
            report.AppendLine(CultureInfo.InvariantCulture, $"empty clipboard: pins {before} -> {Pins.Count} (a tray notice instead)");

            ShowHistory();
            if (_historyWindow is { } window)
            {
                await Task.Delay(1500);
                await window.DevSearch("invoice");
                before = Pins.Count;
                window.DevPinFirst();
                report.AppendLine(CultureInfo.InvariantCulture, $"pin from filtered history: pins {before} -> {Pins.Count}");
                await Task.Delay(800);
                if (Pins.Count > before) { DevRender(Pins[^1], Path.Combine(devFolder, "pin-history.png")); Pins[^1].Close(); }
                await window.DevSearch("");
            }
            File.WriteAllText(Path.Combine(devFolder, "pin-scenario.txt"), report.ToString());
        }
        catch (Exception ex) { Logging.Log.Error("Dev pin scenario failed", ex); }
    }

    /// <summary>Окно целиком в PNG (без снимка экрана).</summary>
    private static void DevRender(Window window, string path)
    {
        window.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(window);
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX), (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(window);
        File.WriteAllBytes(path, ClipboardService.EncodePng(rtb));
    }

    /// <summary>Время распознавания каждого снимка тестовой истории: обычное (как у индекса) и без проходов с увеличением.</summary>
    private async Task WriteDevOcrTimingAsync(string path)
    {
        if (History is not { } store) return;
        var items = await _imageSaver.Queue.Enqueue(store.List);
        // Снимок во весь экран 2560×1440 с мелким текстом (в историю не попадает) — первым, с прогревом движков.
        // Рисуется здесь: визуалам WPF нужен STA-поток.
        var large = Path.Combine(Path.GetDirectoryName(path)!, "history-search-large.png");
        var lines = Enumerable.Range(1, 60).Select(i => i % 2 == 0
            ? $"{i:00}. The quick brown fox jumps over the lazy dog; build #{1000 + i} finished with 0 errors and {i % 5} warnings."
            : $"{i:00}. Съешь же ещё этих мягких французских булок, да выпей чаю; заказ № {5000 + i} оплачен.").ToArray();
        File.WriteAllBytes(large, ClipboardService.EncodePng(RenderDevShot("Large screenshot 2560x1440", lines, 2560, 1440, 15, 22)));
        var report = await Task.Run(async () =>
        {
            var sb = new StringBuilder();
            var recognizer = new WindowsOcrTextRecognizer();
            sb.AppendLine(CultureInfo.InvariantCulture, $"languages: {string.Join(", ", OcrService.LanguageTags())}");
            var paths = items.Take(12).Select(i => i.Path).Prepend(large).Prepend(large).ToList();
            for (int n = 0; n < paths.Count; n++)
            {
                var image = HistoryItemViewModel.LoadThumbnail(paths[n]);
                var full = new HistoryItemViewModel(new HistoryItem(paths[n], DateTime.Now)).LoadFull();
                var watch = Stopwatch.StartNew();
                var text = recognizer.Recognize(paths[n]);
                var index = watch.ElapsedMilliseconds;
                watch.Restart();
                await OcrService.RecognizeAsync(full);
                var adaptive = watch.ElapsedMilliseconds;
                watch.Restart();
                await OcrService.RecognizeAsync(full, adaptive: false);
                var single = watch.ElapsedMilliseconds;
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"{(n == 0 ? "[warm-up] " : "")}{Path.GetFileName(paths[n])} {image.Width}x{image.Height}: index {index} ms " +
                    $"(adaptive {adaptive} ms + per-language single passes), single pass only {single} ms, {text.Text.Length} chars, {text.Alternates?.Count ?? 0} alternates");
            }
            // Прочтения синтетических русских снимков — чтобы видеть, что попадает в поиск.
            foreach (var p in paths.Skip(2))
                if (HistoryText.TryRead(HistoryStore.TextPath(p)) is { } t && t.SearchableText.Contains("Ромашка", StringComparison.Ordinal))
                    sb.AppendLine("ru invoice searchable text:").AppendLine(t.SearchableText);
            return sb.ToString();
        });
        File.WriteAllText(path, report);
    }

    private static BitmapSource RenderDevShot(string title, string[] lines, int width = 880, int height = 420, double fontSize = 20, double lineHeight = 48)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xF0, 0xF2, 0xF5)), null, new Rect(0, 0, width, 64));
            var face = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
            var body = new Typeface("Segoe UI");
            dc.DrawText(Text(title, face, 26), new Point(32, 16));
            double y = 96;
            foreach (var line in lines)
            {
                dc.DrawText(Text(line, body, fontSize), new Point(32, y));
                y += lineHeight;
            }
        }
        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        rtb.Freeze();
        return rtb;

        static FormattedText Text(string s, Typeface face, double size)
            => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, face, size, new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x28)), 1.0);
    }
}

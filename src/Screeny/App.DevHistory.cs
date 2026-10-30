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
            foreach (var (file, query) in new[] { ("all", ""), ("invoice", "invoice total"), ("schet", "ромашка МОСКВА"), ("error", "error"), ("none", "zebra") })
            {
                window.DevSearch(query);
                await Task.Delay(300);
                window.DevRenderTo(Path.Combine(devFolder, $"history-search-{file}.png"));
            }
            window.DevSearch("");
        }
        catch (Exception ex) { Logging.Log.Error("Dev search scenario failed", ex); }
    }

    /// <summary>Время распознавания каждого снимка тестовой истории: обычное (как у индекса) и без проходов с увеличением.</summary>
    private async Task WriteDevOcrTimingAsync(string path)
    {
        if (History is not { } store) return;
        var items = await _imageSaver.Queue.Enqueue(store.List);
        var report = await Task.Run(async () =>
        {
            var sb = new StringBuilder();
            var recognizer = new WindowsOcrTextRecognizer();
            sb.AppendLine(CultureInfo.InvariantCulture, $"languages: {string.Join(", ", OcrService.LanguageTags())}");
            foreach (var item in items.Take(12))
            {
                var image = HistoryItemViewModel.LoadThumbnail(item.Path);
                var full = new HistoryItemViewModel(item).LoadFull();
                var watch = Stopwatch.StartNew();
                var text = recognizer.Recognize(item.Path);
                var adaptive = watch.ElapsedMilliseconds;
                watch.Restart();
                await OcrService.RecognizeAsync(full, adaptive: false);
                var single = watch.ElapsedMilliseconds;
                sb.AppendLine(CultureInfo.InvariantCulture,
                    $"{Path.GetFileName(item.Path)} {image.Width}x{image.Height}: index {adaptive} ms, single pass {single} ms, {text.Length} chars");
            }
            return sb.ToString();
        });
        File.WriteAllText(path, report);
    }

    private static BitmapSource RenderDevShot(string title, string[] lines)
    {
        const int width = 880, height = 420;
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
                dc.DrawText(Text(line, body, 20), new Point(32, y));
                y += 48;
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

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Screeny.History;
using Screeny.Modules;

namespace Screeny;

/// <summary>
/// <c>--dev --module-source releases --history --scenario semantic</c>: проверка смыслового поиска целиком, без настоящих
/// снимков. Своя история в памяти процесса (папка <c>%LocalAppData%\Screeny\Dev\HistorySemantic</c>, настройки не меняются):
/// туда через обычный путь сохранения кладутся синтетические снимки спайка (<c>tools/semantic-module/work/testset</c>, см.
/// tools/semantic-module/README.md, или папка из <c>SCREENY_SEMANTIC_TESTSET</c>). Модуль ставится из
/// <c>--module-source</c>, если его нет. Когда текст распознан и векторы посчитаны, окно истории рисуется в PNG для запросов
/// «котик», «cat», «график», «счёт», «error dialog» (<c>%LocalAppData%\Screeny\Dev\history-semantic-*.png</c>), а итог —
/// найденное по каждому запросу, время индексации и память — в <c>history-semantic.txt</c> рядом.
/// </summary>
public partial class App
{
    private static readonly (string File, string Query)[] DevSemanticQueries =
        [("kotik", "котик"), ("cat", "cat"), ("grafik", "график"), ("schet", "счёт"), ("error", "error dialog")];

    private async void RunDevSemanticScenario()
    {
        var devRoot = Path.GetDirectoryName(Screeny.Settings.SettingsStore.DevHistoryFolder())!; // %LocalAppData%\Screeny\Dev
        var report = new StringBuilder();
        try
        {
            if (FindDevTestset() is not { } testset) { report.AppendLine("testset not found: tools/semantic-module/work/testset or SCREENY_SEMANTIC_TESTSET"); return; }
            if (_semanticModule is not { } module) return;
            if (module.ReadyFolder is null)
            {
                var finished = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
                module.Finished += (ok, error, _) => finished.TrySetResult(ok ? null : error ?? "cancelled");
                var watch0 = Stopwatch.StartNew();
                module.StartDownload();
                if (await finished.Task is { } error) { report.AppendLine("module install failed: " + error); return; }
                report.AppendLine(CultureInfo.InvariantCulture, $"module installed from --module-source in {watch0.ElapsedMilliseconds} ms");
            }

            // Своя история: настройки dev-экземпляра (и общая тестовая история) не меняются.
            var folder = Path.Combine(devRoot, "HistorySemantic");
            History = new HistoryStore(folder, 100);
            _textIndexer?.Stop();
            _semanticIndexer?.Stop();
            var names = JsonDocument.Parse(File.ReadAllText(Path.Combine(testset, "testset.json"))).RootElement.GetProperty("images")
                .EnumerateArray().Select(e => e.GetString()!).ToList();
            var map = Path.Combine(folder, "testset-map.txt");
            var byPath = File.Exists(map)
                ? File.ReadAllLines(map).Select(l => l.Split('\t')).Where(p => p.Length == 2 && File.Exists(p[0])).ToDictionary(p => p[0], p => p[1], StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var watch = Stopwatch.StartNew();
            if (byPath.Count != names.Count)
            {
                History.Clear();
                byPath.Clear();
                foreach (var name in names)
                {
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.UriSource = new Uri(Path.Combine(testset, name));
                    bmp.EndInit();
                    bmp.Freeze();
                    if ((await SaveImageAsync(bmp, null, null, null, null)).HistoryPath is { } p) byPath[p] = name;
                    await Task.Delay(5); // разные штампы времени
                }
                File.WriteAllLines(map, byPath.Select(kv => kv.Key + "\t" + kv.Value));
            }
            // Текст старых (уже сохранённых) снимков — догоняющим проходом; векторы — как только есть текст.
            if (TextIndexOn) _textIndexer?.Start();
            StartSemanticIndex(TimeSpan.Zero);
            var store = History;
            while (watch.Elapsed < TimeSpan.FromMinutes(20))
            {
                var items = await _imageSaver.Queue.Enqueue(store.List);
                if (items.All(i => i.VectorPath is not null)) break;
                await Task.Delay(1000);
                if (watch.Elapsed.TotalSeconds % 30 < 1) Logging.Log.Error($"Dev semantic scenario: {items.Count(i => i.VectorPath is not null)} of {items.Count} indexed");
            }
            using (var process = Process.GetCurrentProcess())
                report.AppendLine(CultureInfo.InvariantCulture,
                    $"{names.Count} screenshots: text + vectors in {watch.Elapsed.TotalSeconds:F0} s ({watch.Elapsed.TotalMilliseconds / names.Count:F0} ms per screenshot wall clock, " +
                    $"OCR included); peak working set {process.PeakWorkingSet64 / 1048576} MB; progress {SemanticProgress}");

            // Найденное по запросам — тем же объединением, что окно истории.
            var search = new HistorySearch(new TextSearchProvider(), _semanticSearch!);
            var entries = (await _imageSaver.Queue.Enqueue(store.List))
                .Select(i => new HistorySearchEntry(i.Path, i.CreatedAt, HistoryText.TryRead(i.TextPath)?.SearchableText)).ToList();
            foreach (var (_, query) in DevSemanticQueries)
            {
                var sw = Stopwatch.StartNew();
                var hits = await search.SearchAsync(query, entries) ?? [];
                var found = string.Join(", ", hits.Select(h => byPath.GetValueOrDefault(h.Path, Path.GetFileName(h.Path)) + " " + h.Score.ToString("F3", CultureInfo.InvariantCulture)));
                report.AppendLine(CultureInfo.InvariantCulture, $"{query} ({sw.ElapsedMilliseconds} ms): {found}");
            }

            ShowHistory();
            if (_historyWindow is not { } window) return;
            await Task.Delay(2000); // тексты и миниатюры загружаются в фоне
            foreach (var (file, query) in DevSemanticQueries)
            {
                await window.DevSearch(query);
                await Task.Delay(800);
                window.DevRenderTo(Path.Combine(devRoot, $"history-semantic-{file}.png"));
            }
        }
        catch (Exception ex)
        {
            Logging.Log.Error("Dev semantic scenario failed", ex);
            report.AppendLine("failed: " + ex.Message);
        }
        finally { File.WriteAllText(Path.Combine(devRoot, "history-semantic.txt"), report.ToString()); }
    }

    /// <summary>Папка тестового набора спайка: из переменной окружения или вверх от папки сборки до корня репозитория.</summary>
    private static string? FindDevTestset()
    {
        if (Environment.GetEnvironmentVariable("SCREENY_SEMANTIC_TESTSET") is { Length: > 0 } env && File.Exists(Path.Combine(env, "testset.json"))) return env;
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "tools", "semantic-module", "work", "testset");
            if (File.Exists(Path.Combine(candidate, "testset.json"))) return candidate;
        }
        return null;
    }
}

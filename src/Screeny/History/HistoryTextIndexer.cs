using System.Windows.Media.Imaging;
using Screeny.Logging;
using Screeny.Ocr;

namespace Screeny.History;

/// <summary>Распознавание текста снимка для поиска; в тестах подменяется.</summary>
public interface IHistoryTextRecognizer
{
    /// <summary>Чем сейчас распознавался бы текст; null — распознавание недоступно (в Windows нет языков с OCR).</summary>
    HistoryTextMarker? CurrentMarker();

    /// <summary>Текст снимка (пустой — текста нет). Блокирующий вызов, выполняется в фоновом потоке.</summary>
    RecognizedText Recognize(string pngPath);
}

/// <summary>Итог распознавания и другие прочтения отдельными языками (только отличающиеся от итога).</summary>
public sealed record RecognizedText(string Text, IReadOnlyList<string>? Alternates = null);

/// <summary>Быстрое распознавание Windows (<see cref="OcrService"/>); точный модуль для индекса не нужен.</summary>
public sealed class WindowsOcrTextRecognizer : IHistoryTextRecognizer
{
    public const string Engine = "windows-ocr";

    public HistoryTextMarker? CurrentMarker()
    {
        var tags = OcrService.LanguageTags();
        return tags.Count == 0 ? null : new HistoryTextMarker(Engine, HistoryTextIndexer.IndexVersion, tags);
    }

    public RecognizedText Recognize(string pngPath)
    {
        BitmapSource image;
        using (var ms = new MemoryStream(File.ReadAllBytes(pngPath), writable: false))
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            image = bmp;
        }
        // Продолжения WinRT идут в пуле потоков, а не в этом: ожидание здесь не взаимоблокируется.
        var text = OcrService.RecognizeAsync(image).GetAwaiter().GetResult() ?? "";
        var alternates = OcrService.RecognizeEachLanguageAsync(image).GetAwaiter().GetResult()
            .Where(t => !string.IsNullOrWhiteSpace(t) && t != text)
            .Distinct()
            .ToList();
        return new RecognizedText(text, alternates);
    }
}

/// <summary>
/// Фоновое распознавание текста снимков истории для поиска (sidecar <see cref="HistoryText"/>). Новые снимки
/// (<see cref="Add"/>) идут первыми, затем — догоняющий проход по старым (<see cref="Start"/>): от новых к старым, по одному,
/// с паузой между снимками, на потоке с низким приоритетом. Пока открыт оверлей (<c>paused</c>), ничего не начинается.
/// Проход возобновляемый: снимки с текстом текущей отметки (<see cref="HistoryTextMarker"/>) пропускаются, поэтому после
/// перезапуска он продолжается с того места, где остановился. Без функций «История» или «Распознавание» (<c>enabled</c>)
/// не делает ничего. Распознанный текст в журнал не пишется.
/// </summary>
public sealed class HistoryTextIndexer
{
    /// <summary>Версия индексатора в отметке: поднять — и все снимки распознаются заново.</summary>
    public const int IndexVersion = 2;

    private readonly Func<HistoryStore?> _store;
    private readonly Func<bool> _enabled, _paused;
    private readonly IHistoryTextRecognizer _recognizer;
    private readonly BackgroundQueue _files;
    private readonly BackgroundQueue _ocr = new();
    private readonly TimeSpan _throttle, _freshDelay, _pausePoll;
    private readonly object _gate = new();
    private readonly LinkedList<string> _fresh = new();
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task _run = Task.CompletedTask;
    private bool _running, _backfillRequested;

    /// <param name="store">Текущее хранилище истории (папка может смениться).</param>
    /// <param name="enabled">История и распознавание включены (читается перед каждым снимком).</param>
    /// <param name="paused">Сейчас нельзя начинать распознавание (открыт оверлей).</param>
    /// <param name="files">Общая очередь файловых операций истории: запись текста не пересекается с удалением и ротацией.</param>
    /// <param name="throttle">Пауза между снимками догоняющего прохода.</param>
    /// <param name="freshDelay">Задержка перед распознаванием нового снимка (не мешать копированию).</param>
    public HistoryTextIndexer(Func<HistoryStore?> store, Func<bool> enabled, Func<bool> paused, IHistoryTextRecognizer recognizer,
        BackgroundQueue files, TimeSpan throttle, TimeSpan freshDelay, TimeSpan? pausePoll = null)
    {
        _store = store;
        _enabled = enabled;
        _paused = paused;
        _recognizer = recognizer;
        _files = files;
        _throttle = throttle;
        _freshDelay = freshDelay;
        _pausePoll = pausePoll ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>Снимок распознан: путь и текст для поиска (<see cref="HistoryText.SearchableText"/>). Вызывается из фонового потока.</summary>
    public event Action<string, string>? Indexed;

    /// <summary>Догоняющий проход по папке (после задержки). Повторный вызов во время прохода ничего не ломает.</summary>
    public void Start(TimeSpan delay = default)
    {
        lock (_gate)
        {
            _backfillRequested = true;
            EnsureRunning(delay);
        }
    }

    /// <summary>Новый снимок истории: распознаётся раньше старых.</summary>
    public void Add(string pngPath)
    {
        lock (_gate)
        {
            _fresh.AddLast(pngPath);
            EnsureRunning(_freshDelay);
        }
    }

    /// <summary>Останавливает работу и забывает очередь; начатое распознавание одного снимка доработает, но его текст не запишется.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _cts?.Cancel();
            _cts = null;
            _running = false;
            _fresh.Clear();
            _backfillRequested = false;
        }
    }

    /// <summary>Текущий проход (для тестов): завершается, когда очередь пуста или работа остановлена.</summary>
    public Task WhenIdle()
    {
        lock (_gate) return _run;
    }

    private void EnsureRunning(TimeSpan delay)
    {
        if (_running) return;
        _running = true;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _run = Task.Run(() => RunAsync(delay, token));
    }

    /// <summary>Выход из цикла: только если его не остановили (иначе флаг уже принадлежит новому проходу).</summary>
    private bool TryFinish(CancellationToken token, bool onlyIfIdle)
    {
        lock (_gate)
        {
            if (token.IsCancellationRequested) return true;
            if (onlyIfIdle && (_fresh.Count > 0 || _backfillRequested)) return false;
            _running = false;
            return true;
        }
    }

    private async Task RunAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token);
            HistoryTextMarker? marker = null;
            Queue<string>? backlog = null;
            while (!token.IsCancellationRequested)
            {
                if (!_enabled()) { TryFinish(token, onlyIfIdle: false); return; }
                if (_paused()) { await Task.Delay(_pausePoll, token); continue; }
                marker ??= _recognizer.CurrentMarker();
                if (marker is null) { TryFinish(token, onlyIfIdle: false); return; } // нет языков OCR

                string? path;
                bool fresh;
                bool backfill;
                lock (_gate)
                {
                    path = _fresh.First?.Value;
                    if (path is not null) _fresh.RemoveFirst();
                    fresh = path is not null;
                    backfill = _backfillRequested;
                    _backfillRequested = false;
                }
                if (backfill) backlog = await BacklogAsync(marker);
                if (path is null && backlog is { Count: > 0 }) path = backlog.Dequeue();
                if (path is null)
                {
                    if (TryFinish(token, onlyIfIdle: true)) return;
                    continue;
                }
                await IndexOneAsync(path, marker, token);
                if (!fresh && _throttle > TimeSpan.Zero) await Task.Delay(_throttle, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("History text indexing failed", ex);
            TryFinish(token, onlyIfIdle: false);
        }
    }

    /// <summary>Снимки без текста текущей отметки, от новых к старым.</summary>
    private async Task<Queue<string>> BacklogAsync(HistoryTextMarker marker)
    {
        var store = _store();
        if (store is null) return new Queue<string>();
        var items = await _files.Enqueue(store.List);
        return await Task.Run(() =>
        {
            var queue = new Queue<string>();
            foreach (var item in items)
            {
                lock (_gate) if (_failed.Contains(item.Path)) continue;
                if (NeedsIndex(item, marker)) queue.Enqueue(item.Path);
            }
            return queue;
        });
    }

    /// <summary>Снимок ещё не распознан или распознан другим движком, версией или набором языков.</summary>
    public static bool NeedsIndex(HistoryItem item, HistoryTextMarker marker)
        => item.TextPath is null || HistoryText.TryRead(item.TextPath) is not { } text || !text.Marker.SameAs(marker);

    private async Task IndexOneAsync(string path, HistoryTextMarker marker, CancellationToken token)
    {
        var store = _store();
        if (store is null || !File.Exists(path)) return;
        RecognizedText recognized;
        try { recognized = await _ocr.Enqueue(() => RecognizeAtLowPriority(path)); }
        catch (Exception ex)
        {
            // Битый или занятый файл: в этом сеансе больше не пробуем; текст снимка в журнал не пишется.
            Log.Error($"History text recognition failed: {path}", ex);
            lock (_gate) _failed.Add(path);
            return;
        }
        if (token.IsCancellationRequested || !_enabled()) return;
        var text = new HistoryText(recognized.Text, marker, recognized.Alternates is { Count: > 0 } alt ? alt : null);
        bool written;
        try { written = await _files.Enqueue(() => store.WriteText(path, text)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"History text write failed: {path}", ex);
            lock (_gate) _failed.Add(path);
            return;
        }
        if (written) Indexed?.Invoke(path, text.SearchableText);
    }

    private RecognizedText RecognizeAtLowPriority(string path)
    {
        var thread = Thread.CurrentThread;
        var priority = thread.Priority;
        thread.Priority = ThreadPriority.Lowest;
        try { return _recognizer.Recognize(path); }
        finally { thread.Priority = priority; }
    }
}

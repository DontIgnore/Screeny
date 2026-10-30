using System.Diagnostics;
using Screeny.History;
using Screeny.Logging;

namespace Screeny.Semantic;

/// <summary>Векторы снимка для индекса; в тестах подменяется.</summary>
public interface ISemanticItemEncoder
{
    /// <summary>Чем сейчас считались бы векторы.</summary>
    SemanticMarker Marker { get; }

    /// <summary>
    /// Векторы снимка: картинка (или прежняя из <paramref name="reuseImage"/>) и распознанный текст (нет текста — нет вектора).
    /// null — модель недоступна (модуль удалён, не загрузился). Блокирующий вызов, выполняется в фоновом потоке.
    /// </summary>
    SemanticVectors? Encode(string pngPath, string? text, float[]? reuseImage);
}

/// <summary>Векторы через <see cref="SemanticService"/>: время и память каждого снимка — в журнал (для оценки стоимости).</summary>
public sealed class ServiceItemEncoder(SemanticService service) : ISemanticItemEncoder
{
    public SemanticMarker Marker => SemanticService.CurrentMarker;

    public SemanticVectors? Encode(string pngPath, string? text, float[]? reuseImage)
        => service.Use(e =>
        {
            var sw = Stopwatch.StartNew();
            string size = "";
            var image = reuseImage;
            if (image is null)
            {
                var rgb = RgbImage.Load(pngPath);
                size = $"{rgb.Width}x{rgb.Height} ";
                image = e.EmbedImage(rgb);
            }
            long imageMs = sw.ElapsedMilliseconds;
            var textVector = string.IsNullOrWhiteSpace(text) ? null : e.EmbedDocument(text);
            using var process = Process.GetCurrentProcess();
            Log.Error($"Semantic index {size}{(reuseImage is null ? $"image {imageMs} ms" : "image reused")}, text {sw.ElapsedMilliseconds - imageMs} ms" +
                      $"{(textVector is null ? " (none)" : "")}, working set {process.WorkingSet64 / 1048576} MB");
            return new SemanticVectors(Marker, SemanticVectors.HashText(text), image, textVector);
        });
}

/// <summary>
/// Фоновая индексация истории для смыслового поиска (sidecar <see cref="SemanticVectors"/>). Новые снимки (<see cref="Add"/>,
/// после того как у них появился распознанный текст) идут первыми, затем — догоняющий проход (<see cref="Start"/>) от новых
/// к старым. По одному снимку, с паузой между снимками, в потоке с низким приоритетом (сам ORT считает ещё одним своим
/// потоком обычного приоритета: <see cref="SemanticService.Threads"/>). Пока открыт оверлей (<c>paused</c>), ничего не
/// начинается. Проход возобновляемый: снимки с актуальными векторами пропускаются. Снимок без распознанного текста ждёт его
/// (<c>textExpected</c>), если распознавание вообще работает; иначе индексируется по одной картинке. Сменилась модель или
/// бюджет картинки — снимок индексируется заново; сменился только текст — пересчитывается только его вектор.
/// Без модуля или без функций «История» и «Распознавание» (<c>enabled</c>) не делает ничего.
/// </summary>
public sealed class SemanticIndexer
{
    private readonly Func<HistoryStore?> _store;
    private readonly Func<bool> _enabled, _paused, _textExpected;
    private readonly ISemanticItemEncoder _encoder;
    private readonly BackgroundQueue _files;
    private readonly BackgroundQueue _work = new();
    private readonly TimeSpan _throttle, _pausePoll;
    private readonly object _gate = new();
    private readonly LinkedList<string> _fresh = new();
    private readonly HashSet<string> _failed = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _cts;
    private Task _run = Task.CompletedTask;
    private bool _running, _backfillRequested;
    private int _done, _total;

    /// <param name="store">Текущее хранилище истории (папка может смениться).</param>
    /// <param name="enabled">Модуль готов, история и распознавание включены (читается перед каждым снимком).</param>
    /// <param name="paused">Сейчас нельзя начинать (открыт оверлей).</param>
    /// <param name="textExpected">Распознавание текста работает: снимок без текста ждёт его, а не индексируется без него.</param>
    /// <param name="files">Общая очередь файловых операций истории: запись не пересекается с удалением и ротацией.</param>
    /// <param name="throttle">Пауза между снимками.</param>
    public SemanticIndexer(Func<HistoryStore?> store, Func<bool> enabled, Func<bool> paused, Func<bool> textExpected,
        ISemanticItemEncoder encoder, BackgroundQueue files, TimeSpan throttle, TimeSpan? pausePoll = null)
    {
        _store = store;
        _enabled = enabled;
        _paused = paused;
        _textExpected = textExpected;
        _encoder = encoder;
        _files = files;
        _throttle = throttle;
        _pausePoll = pausePoll ?? TimeSpan.FromSeconds(1);
    }

    /// <summary>Снимок проиндексирован: путь и векторы. Вызывается из фонового потока.</summary>
    public event Action<string, SemanticVectors>? Indexed;

    /// <summary>Поменялся счёт «N из M» (<see cref="Progress"/>). Вызывается из фонового потока.</summary>
    public event Action? ProgressChanged;

    /// <summary>Сколько снимков с актуальными векторами из скольких (по последнему проходу по папке).</summary>
    public (int Done, int Total) Progress { get { lock (_gate) return (_done, _total); } }

    /// <summary>Догоняющий проход по папке (после задержки). Повторный вызов во время прохода ничего не ломает.</summary>
    public void Start(TimeSpan delay = default)
    {
        lock (_gate)
        {
            _backfillRequested = true;
            EnsureRunning(delay);
        }
    }

    /// <summary>Новый снимок (его текст уже распознан или не ожидается): индексируется раньше старых.</summary>
    public void Add(string pngPath)
    {
        lock (_gate)
        {
            if (_fresh.Contains(pngPath)) return;
            _fresh.AddLast(pngPath);
            EnsureRunning(TimeSpan.Zero);
        }
    }

    /// <summary>Останавливает работу и забывает очередь; начатый снимок доработает, но его векторы не запишутся.</summary>
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

    /// <summary>Текущий проход (для тестов и dev-сценария): завершается, когда очередь пуста или работа остановлена.</summary>
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
            Queue<string>? backlog = null;
            bool indexed = false;
            while (!token.IsCancellationRequested)
            {
                if (!_enabled()) { TryFinish(token, onlyIfIdle: false); return; }
                if (_paused()) { await Task.Delay(_pausePoll, token); continue; }

                string? path;
                bool fresh, backfill;
                lock (_gate)
                {
                    path = _fresh.First?.Value;
                    if (path is not null) _fresh.RemoveFirst();
                    fresh = path is not null;
                    backfill = _backfillRequested;
                    _backfillRequested = false;
                }
                if (backfill) backlog = await BacklogAsync();
                if (path is null && backlog is { Count: > 0 }) path = backlog.Dequeue();
                if (path is null)
                {
                    // Конец прохода: счёт «N из M» — заново по папке (новые снимки, удалённые, дождавшиеся текста).
                    if (indexed) { indexed = false; await BacklogAsync(); }
                    if (TryFinish(token, onlyIfIdle: true)) return;
                    continue;
                }
                if (await IndexOneAsync(path, token) is not { } done) { TryFinish(token, onlyIfIdle: false); return; }
                indexed |= done;
                if (!fresh && _throttle > TimeSpan.Zero) await Task.Delay(_throttle, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("Semantic indexing failed", ex);
            TryFinish(token, onlyIfIdle: false);
        }
    }

    /// <summary>Что делать со снимком.</summary>
    public enum Need { None, WaitText, Text, Full }

    /// <summary>
    /// Векторов нет или они другой модели/бюджета → <see cref="Need.Full"/>; текст ещё не распознан, а распознавание работает →
    /// <see cref="Need.WaitText"/>; текст поменялся → <see cref="Need.Text"/>; иначе всё актуально.
    /// </summary>
    public static Need Check(HistoryItem item, SemanticMarker marker, bool textExpected, out string? text, out SemanticVectors? existing)
    {
        existing = SemanticVectors.TryRead(item.VectorPath);
        var historyText = HistoryText.TryRead(item.TextPath);
        text = SemanticVectors.UsableText(historyText?.SearchableText);
        if (historyText is null && textExpected) return Need.WaitText;
        if (existing is null || existing.Marker != marker || existing.Image is null) return Need.Full;
        return existing.TextHash != SemanticVectors.HashText(text) ? Need.Text : Need.None;
    }

    /// <summary>Снимки, которым нужны векторы и у которых уже есть всё для них, от новых к старым; заодно — счёт «N из M».</summary>
    private async Task<Queue<string>> BacklogAsync()
    {
        var store = _store();
        if (store is null) return new Queue<string>();
        var items = await _files.Enqueue(store.List);
        bool textExpected = _textExpected();
        var queue = await Task.Run(() =>
        {
            var q = new Queue<string>();
            int done = 0;
            foreach (var item in items)
            {
                var need = Check(item, _encoder.Marker, textExpected, out _, out _);
                if (need == Need.None) { done++; continue; }
                lock (_gate) if (_failed.Contains(item.Path)) continue;
                if (need != Need.WaitText) q.Enqueue(item.Path);
            }
            lock (_gate) { _done = done; _total = items.Count; }
            return q;
        });
        ProgressChanged?.Invoke();
        return queue;
    }

    /// <summary>
    /// true — векторы записаны, false — снимок пропущен; null — модель недоступна: проход останавливается (повторится при
    /// следующем <see cref="Start"/>).
    /// </summary>
    private async Task<bool?> IndexOneAsync(string path, CancellationToken token)
    {
        var store = _store();
        if (store is null || !File.Exists(path)) return false;
        var item = new HistoryItem(path, default)
        {
            TextPath = File.Exists(HistoryStore.TextPath(path)) ? HistoryStore.TextPath(path) : null,
            VectorPath = File.Exists(HistoryStore.VectorPath(path)) ? HistoryStore.VectorPath(path) : null,
        };
        var need = Check(item, _encoder.Marker, _textExpected(), out var text, out var existing);
        if (need is Need.None or Need.WaitText) return false;
        SemanticVectors? vectors;
        try { vectors = await _work.Enqueue(() => EncodeAtLowPriority(path, text, need == Need.Text ? existing!.Image : null)); }
        catch (Exception ex)
        {
            // Битый или занятый файл, картинка слишком мала: в этом сеансе больше не пробуем.
            Log.Error($"Semantic indexing failed: {path}", ex);
            lock (_gate) _failed.Add(path);
            return false;
        }
        if (vectors is null) return null;
        if (token.IsCancellationRequested || !_enabled()) return false;
        bool written;
        try { written = await _files.Enqueue(() => store.WriteVectors(path, vectors)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error($"Semantic vectors write failed: {path}", ex);
            lock (_gate) _failed.Add(path);
            return false;
        }
        if (!written) return false;
        lock (_gate)
        {
            _done++;
            _total = Math.Max(_total, _done);
        }
        Indexed?.Invoke(path, vectors);
        ProgressChanged?.Invoke();
        return true;
    }

    private SemanticVectors? EncodeAtLowPriority(string path, string? text, float[]? reuseImage)
    {
        var thread = Thread.CurrentThread;
        var priority = thread.Priority;
        thread.Priority = ThreadPriority.Lowest;
        try { return _encoder.Encode(path, text, reuseImage); }
        finally { thread.Priority = priority; }
    }
}

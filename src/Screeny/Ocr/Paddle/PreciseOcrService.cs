using Screeny.Logging;
using Screeny.Modules;

namespace Screeny.Ocr.Paddle;

/// <summary>Уточнение текста для режима текста (оверлей не знает, откуда оно берётся).</summary>
public interface IOcrRefiner
{
    /// <summary>Точное распознавание активно и модуль не сломан: уточнение стоит запускать.</summary>
    bool IsActive { get; }

    /// <summary>Проверить файлы модуля и загрузить сессии, если их ещё нет. false — модуль не загрузился (причина в журнале).</summary>
    Task<bool> PrepareAsync(CancellationToken ct);

    /// <summary>Раскладка в пикселях <paramref name="image"/> или null (неактивно, ошибка загрузки — уже в журнале).</summary>
    Task<OcrLayout?> RecognizeAsync(OcrImage image, CancellationToken ct);

    /// <summary>Пользователь вошёл в режим текста (Ctrl+T): повод напомнить про устаревший модуль.</summary>
    void TextModeUsed();
}

/// <summary>
/// Точное распознавание для всего приложения. Сессии ORT создаются при первом уточнении и освобождаются через
/// <see cref="IdleTimeout"/> без работы: на полном экране арена ORT держит до ≈1 ГБ, фоновому приложению в трее это ни к чему.
/// Прогоны идут по одному (семафор), освобождение по таймеру берёт тот же семафор и не попадает на идущий прогон.
/// Каждый раз, когда сессии создаются заново (первый раз и после освобождения), файлы модуля сверяются с <c>module.json</c>,
/// а сам он — с SHA256, зашитым в приложении. Ошибка загрузки (повреждённые файлы, нет или устарел рантайм VC++,
/// антивирус) выключает уточнение до перезапуска или переустановки — режим текста работает на Windows OCR.
/// </summary>
public sealed class PreciseOcrService : IOcrRefiner, IDisposable
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);

    private readonly Func<string?> _activeFolder;
    private readonly Action _textModeUsed;
    private readonly ModuleSpec _spec;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _idle;
    private readonly (int Par, int Threads) _split = PaddlePipeline.ThreadSplit(Environment.ProcessorCount);
    private PaddleEngine? _engine;
    private string? _engineFolder;
    private volatile string? _loadError;
    private bool _disposed;

    /// <param name="activeFolder">Папка модуля, если точное распознавание активно (<see cref="PreciseOcrRules.IsActive"/>), иначе null.</param>
    /// <param name="textModeUsed">Вызывается при каждом входе в режим текста.</param>
    /// <param name="spec">Что проверять в папке; по умолчанию — модуль этой сборки (<see cref="OcrModule.Spec"/>).</param>
    public PreciseOcrService(Func<string?> activeFolder, Action textModeUsed, ModuleSpec? spec = null)
    {
        _activeFolder = activeFolder;
        _textModeUsed = textModeUsed;
        _spec = spec ?? OcrModule.Spec;
        _idle = new Timer(_ => ReleaseIdle(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Модуль не загрузился в этом процессе: текст для настроек. null — ошибок не было.</summary>
    public string? LoadError => _loadError;

    public bool IsActive => _loadError is null && !_disposed && _activeFolder() is not null;

    public void TextModeUsed() => _textModeUsed();

    /// <summary>Модуль переустановлен или удалён: прежняя ошибка больше не в силе.</summary>
    public void Reset()
    {
        _loadError = null;
        ReleaseIdle();
    }

    public async Task<bool> PrepareAsync(CancellationToken ct)
        => await UseEngineAsync(engine => true, ct);

    public Task<OcrLayout?> RecognizeAsync(OcrImage image, CancellationToken ct)
        => UseEngineAsync<OcrLayout?>(engine =>
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var layout = engine.Run(image, ct);
            Log.Error($"Precise OCR {image.W}x{image.H}: {sw.ElapsedMilliseconds} ms, {layout.WordCount} words ({engine.Stats}, par={_split.Par} threads={_split.Threads})");
            return layout;
        }, ct);

    /// <summary>Под семафором, в фоне: сессии на месте → <paramref name="work"/>. Модуль неактивен или не загрузился — default.</summary>
    private async Task<T?> UseEngineAsync<T>(Func<PaddleEngine, T> work, CancellationToken ct)
    {
        if (_activeFolder() is not { } folder || _loadError is not null) return default;
        await _gate.WaitAsync(ct);
        try
        {
            if (_disposed) return default;
            _idle.Change(Timeout.Infinite, Timeout.Infinite);
            return await Task.Run(() => EnsureEngine(folder) is { } engine ? work(engine) : default, ct);
        }
        finally
        {
            if (!_disposed) _idle.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
            _gate.Release();
        }
    }

    /// <summary>Сессии на месте (под семафором). null — модуль не загрузился, причина в <see cref="LoadError"/>.</summary>
    private PaddleEngine? EnsureEngine(string folder)
    {
        if (_engine is not null && _engineFolder == folder) return _engine;
        _engine?.Dispose();
        _engine = null;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            // Каждый раз перед созданием сессий, а не раз за процесс: после освобождения файлы могли подменить.
            if (ModuleManifest.Verify(folder, _spec.Id, _spec.Version, _spec.ManifestSha256) is { } error)
            {
                Fail(Localization.Loc.F("PreciseOcr.FilesDamaged", error), null);
                return null;
            }
            long verified = sw.ElapsedMilliseconds;
            _engine = PaddleEngine.Load(folder, _split.Par, _split.Threads);
            _engineFolder = folder;
            Log.Error($"Precise OCR sessions loaded from {folder}: files verified in {verified} ms, loaded in {sw.ElapsedMilliseconds - verified} ms");
            return _engine;
        }
        catch (OrtLoadException ex) { Fail(ex.Message, ex); return null; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Чаще всего — DllNotFoundException: нет рантайма или файл заблокирован антивирусом. Текст исключения .NET
            // пользователю не показываем: он в журнале.
            Fail(ex is DllNotFoundException or BadImageFormatException
                ? Localization.Loc.T("PreciseOcr.OnnxLoadFailed")
                : Localization.Loc.T("PreciseOcr.ModuleLoadFailed"), ex);
            return null;
        }
    }

    private void Fail(string message, Exception? ex)
    {
        _loadError = message;
        Log.Error("Precise OCR disabled for this session: " + message, ex);
    }

    private void ReleaseIdle()
    {
        if (!_gate.Wait(0)) return; // идёт прогон: по его окончании таймер запустится заново
        try
        {
            if (_engine is null) return;
            _engine.Dispose();
            _engine = null;
            _engineFolder = null;
            Log.Error("Precise OCR sessions released (idle)");
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        _disposed = true;
        _idle.Dispose();
        ReleaseIdle();
    }
}

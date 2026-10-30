using Screeny.Logging;
using Screeny.Modules;

namespace Screeny.Semantic;

/// <summary>
/// Модель смыслового поиска для всего приложения: запросы окна истории и фоновая индексация. Сессии ORT создаются при первом
/// обращении и освобождаются через <see cref="IdleTimeout"/> без работы (при индексации до ≈0,8 ГБ — фоновому приложению в
/// трее это ни к чему). <c>InferenceSession.Run</c> потокобезопасен, поэтому запрос не ждёт индексацию снимка: замок
/// охраняет только создание и освобождение сессий, а освобождение не трогает сессии, пока ими кто-то пользуется.
/// Каждый раз перед созданием сессий файлы модуля сверяются с <c>module.json</c>, а сам он — с SHA256 из приложения.
/// Ошибка загрузки выключает смысловой поиск до перезапуска или переустановки (текстовый поиск работает как прежде).
/// </summary>
public sealed class SemanticService : IDisposable
{
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(3);
    /// <summary>Потоков ORT на прогон: индексация не должна занимать процессор целиком.</summary>
    public const int Threads = 2;

    private readonly Func<string?> _activeFolder;
    private readonly ModuleSpec _spec;
    private readonly object _sync = new();
    private readonly Timer _idle;
    private EmbeddingGemmaEncoder? _encoder;
    private string? _encoderFolder;
    private int _users;
    private volatile string? _loadError;
    private volatile bool _disposed;

    /// <param name="activeFolder">Папка модуля, если смысловой поиск включён (модуль нужной версии и функции на месте), иначе null.</param>
    /// <param name="spec">Что проверять в папке; по умолчанию — модуль этой сборки.</param>
    public SemanticService(Func<string?> activeFolder, ModuleSpec? spec = null)
    {
        _activeFolder = activeFolder;
        _spec = spec ?? SemanticModule.Spec;
        _idle = new Timer(_ => ReleaseIdle(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Модуль не загрузился в этом процессе: текст для настроек. null — ошибок не было.</summary>
    public string? LoadError => _loadError;

    public bool IsActive => _loadError is null && !_disposed && _activeFolder() is not null;

    /// <summary>Чем считаются векторы этой сборки.</summary>
    public static SemanticMarker CurrentMarker => new(SemanticModule.Id, SemanticModule.Version, EmbeddingGemmaEncoder.SoftTokens);

    /// <summary>Модуль переустановлен или удалён: прежняя ошибка больше не в силе, сессии — освободить.</summary>
    public void Reset()
    {
        _loadError = null;
        ReleaseIdle();
    }

    /// <summary>Вектор запроса или null (неактивно, ошибка загрузки — уже в журнале).</summary>
    public Task<float[]?> EmbedQueryAsync(string query, CancellationToken ct)
        => Task.Run(() => Use(e => e.EmbedQuery(query)), ct);

    /// <summary>
    /// Блокирующий вызов модели (для фонового индексатора и запросов): сессии на месте → <paramref name="work"/>.
    /// Неактивно или не загрузилось — default.
    /// </summary>
    public T? Use<T>(Func<EmbeddingGemmaEncoder, T> work)
    {
        EmbeddingGemmaEncoder? encoder;
        lock (_sync)
        {
            if (_disposed || _loadError is not null || _activeFolder() is not { } folder) return default;
            _idle.Change(Timeout.Infinite, Timeout.Infinite);
            encoder = EnsureEncoder(folder);
            if (encoder is null) return default;
            _users++;
        }
        try { return work(encoder); }
        finally
        {
            lock (_sync)
            {
                _users--;
                if (_users == 0 && !_disposed) _idle.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
            }
        }
    }

    /// <summary>Сессии на месте (под замком). null — модуль не загрузился, причина в <see cref="LoadError"/>.</summary>
    private EmbeddingGemmaEncoder? EnsureEncoder(string folder)
    {
        if (_encoder is not null && _encoderFolder == folder) return _encoder;
        if (_users > 0) return _encoder; // папка сменилась во время прогона: новая подхватится после него
        _encoder?.Dispose();
        _encoder = null;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            if (ModuleManifest.Verify(folder, _spec.Id, _spec.Version, _spec.ManifestSha256) is { } error)
            {
                Fail(Localization.Loc.F("PreciseOcr.FilesDamaged", error), null);
                return null;
            }
            long verified = sw.ElapsedMilliseconds;
            _encoder = EmbeddingGemmaEncoder.Load(folder, Threads);
            _encoderFolder = folder;
            Log.Error($"Semantic search session loaded from {folder}: files verified in {verified} ms, loaded in {sw.ElapsedMilliseconds - verified} ms");
            return _encoder;
        }
        catch (OrtLoadException ex) { Fail(ex.Message, ex); return null; }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Fail(ex is DllNotFoundException or BadImageFormatException
                ? Localization.Loc.T("PreciseOcr.OnnxLoadFailed")
                : Localization.Loc.T("PreciseOcr.ModuleLoadFailed"), ex);
            return null;
        }
    }

    private void Fail(string message, Exception? ex)
    {
        _loadError = message;
        Log.Error("Semantic search disabled for this session: " + message, ex);
    }

    private void ReleaseIdle()
    {
        lock (_sync)
        {
            if (_users > 0 || _encoder is null) return;
            _encoder.Dispose();
            _encoder = null;
            _encoderFolder = null;
            Log.Error("Semantic search sessions released (idle)");
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _idle.Dispose();
        ReleaseIdle();
    }
}

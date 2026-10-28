using Screeny.Logging;

namespace Screeny.Updates;

public enum UpdateCheckKind { Automatic, Manual }

public enum UpdateSourceKind { None, Github, Folder }

/// <summary>Итог проверки.</summary>
public abstract record UpdateCheckResult
{
    /// <summary>Новее нет.</summary>
    public sealed record UpToDate(string? CurrentVersion) : UpdateCheckResult;
    /// <summary>Есть новая версия, которую стоит показать.</summary>
    public sealed record Available(UpdateCandidate Update) : UpdateCheckResult;
    /// <summary>Новая версия есть, но пользователь её пропустил (только автоматическая проверка).</summary>
    public sealed record Skipped(UpdateCandidate Update) : UpdateCheckResult;
    /// <summary>Копия не умеет обновляться (собрана не через установщик или портативную сборку).</summary>
    public sealed record NotUpdatable : UpdateCheckResult;
    /// <summary>Проверка уже идёт.</summary>
    public sealed record Busy : UpdateCheckResult;
    public sealed record Failed(Exception Error) : UpdateCheckResult;
}

/// <summary>
/// Проверка обновлений: при запуске (через <see cref="StartupDelay"/>) и затем раз в <see cref="Interval"/>, пока
/// включена настройка «Проверять обновления»; ручная — из меню трея. Автоматическая проверка молчит об ошибках
/// (только журнал) и о пропущенной версии; ручная показывает всё.
/// </summary>
public sealed class UpdateService : IDisposable
{
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    private readonly IUpdateClient _client;
    private readonly UpdateStateStore _stateStore;
    private readonly Func<bool> _enabled;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly CancellationTokenSource _stop = new();
    private bool _checking;
    // Один UpdateManager не умеет качать два обновления сразу (тихое в фоне и «Установить» из окна): загрузки по очереди.
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private string? _downloadedVersion;

    /// <summary>Автоматическая проверка нашла версию, которую пользователь не пропускал.</summary>
    public event Action<UpdateCandidate>? UpdateFound;

    public UpdateService(IUpdateClient client, UpdateStateStore stateStore, Func<bool> enabled,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _client = client;
        _stateStore = stateStore;
        _enabled = enabled;
        _delay = delay ?? Task.Delay;
    }

    public IUpdateClient Client => _client;

    /// <summary>
    /// Откуда брать обновления: <c>--update-source</c> (только dev) — папка; обычный запуск — GitHub; тестовый экземпляр
    /// без папки — ниоткуда (ни автоматическая, ни ручная проверка не ходят в сеть).
    /// </summary>
    public static UpdateSourceKind ChooseSource(bool isDev, string? localSource)
        => localSource is not null ? UpdateSourceKind.Folder : isDev ? UpdateSourceKind.None : UpdateSourceKind.Github;

    /// <summary>
    /// Фоновая проверка вообще возможна: копия обновляемая, а тестовый экземпляр — только с явным локальным
    /// источником (<c>--dev --update-source</c>), чтобы dev-запуски никогда не ходили в сеть.
    /// </summary>
    public static bool AutomaticAllowed(bool isDev, bool hasLocalSource, bool canUpdate)
        => canUpdate && (!isDev || hasLocalSource);

    /// <summary>Цикл: пауза после запуска, затем проверка раз в сутки. Настройку читает перед каждой проверкой.</summary>
    public async Task RunAsync()
    {
        var ct = _stop.Token;
        try
        {
            await _delay(StartupDelay, ct);
            while (!ct.IsCancellationRequested)
            {
                if (_enabled() && await CheckAsync(UpdateCheckKind.Automatic, ct) is UpdateCheckResult.Available found)
                    UpdateFound?.Invoke(found.Update);
                await _delay(Interval, ct);
            }
        }
        catch (OperationCanceledException) { /* выход */ }
    }

    public async Task<UpdateCheckResult> CheckAsync(UpdateCheckKind kind, CancellationToken ct = default)
    {
        if (!_client.CanUpdate) return new UpdateCheckResult.NotUpdatable();
        if (_checking) return new UpdateCheckResult.Busy();
        _checking = true;
        try
        {
            var update = await _client.CheckAsync(ct);
            if (update is null) return new UpdateCheckResult.UpToDate(_client.CurrentVersion);
            if (kind == UpdateCheckKind.Automatic && IsSkipped(update.Version)) return new UpdateCheckResult.Skipped(update);
            return new UpdateCheckResult.Available(update);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Нет сети, репозиторий недоступен, лимит GitHub API — в журнал; ручная проверка покажет сообщение.
            Log.Error($"Update check ({kind}) failed", ex);
            return new UpdateCheckResult.Failed(ex);
        }
        finally { _checking = false; }
    }

    /// <summary>
    /// Скачать обновление. Загрузки идут по очереди: вторая ждёт первую. Уже скачанная версия (например, тихой загрузкой)
    /// второй раз не качается — сразу 100 %.
    /// </summary>
    public async Task DownloadAsync(UpdateCandidate update, Action<int>? progress, CancellationToken ct)
    {
        await _downloadLock.WaitAsync(ct);
        try
        {
            if (string.Equals(_downloadedVersion, update.Version, StringComparison.OrdinalIgnoreCase))
            {
                progress?.Invoke(100);
                return;
            }
            await _client.DownloadAsync(update, progress, ct);
            _downloadedVersion = update.Version;
        }
        finally { _downloadLock.Release(); }
    }

    public bool IsSkipped(string version)
        => string.Equals(_stateStore.Load().SkippedVersion, version, StringComparison.OrdinalIgnoreCase);

    /// <summary>«Пропустить эту версию»: автоматическая проверка больше о ней не напоминает (о следующей — напомнит).</summary>
    public bool Skip(string version)
    {
        var state = _stateStore.Load();
        state.SkippedVersion = version;
        return _stateStore.Save(state);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}

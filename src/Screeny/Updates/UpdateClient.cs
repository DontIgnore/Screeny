using Velopack;
using Velopack.Sources;

namespace Screeny.Updates;

/// <summary>Найденное обновление: версия, список изменений (markdown) и объект Velopack для загрузки.</summary>
/// <param name="EarlierNotes">Списки изменений промежуточных версий (новые первыми), если источник знает все версии
/// между текущей и целевой: пустой список — целевая идёт сразу за текущей; null — промежуточные версии неизвестны.</param>
public sealed record UpdateCandidate(string Version, string? NotesMarkdown, object? Handle = null, IReadOnlyList<string?>? EarlierNotes = null)
{
    /// <summary>Списки изменений всех версий от новой к старой: целевая, затем промежуточные.</summary>
    public IEnumerable<string?> AllNotes => EarlierNotes is null ? [NotesMarkdown] : [NotesMarkdown, .. EarlierNotes];
}

/// <summary>Источник обновлений; интерфейс — чтобы проверять <see cref="UpdateService"/> без сети и Velopack.</summary>
public interface IUpdateClient
{
    /// <summary>Копия умеет обновляться: установлена через Setup или запущена из портативной сборки Velopack.</summary>
    bool CanUpdate { get; }
    /// <summary>Текущая версия (из пакета Velopack); null у копии, собранной не через <c>tools/pack.ps1</c>.</summary>
    string? CurrentVersion { get; }
    /// <summary>null — новее нет. Ошибки сети и источника — исключением.</summary>
    Task<UpdateCandidate?> CheckAsync(CancellationToken ct);
    Task DownloadAsync(UpdateCandidate update, Action<int>? progress, CancellationToken ct);
    /// <summary>
    /// Запускает Update.exe, который дождётся выхода приложения (до 60 с), применит обновление и перезапустит Screeny
    /// с <paramref name="restartArgs"/>. Сразу после вызова приложение должно завершиться.
    /// </summary>
    void ApplyAfterExit(UpdateCandidate update, string[] restartArgs);
    /// <summary>
    /// Как <see cref="ApplyAfterExit"/>, но без окон Update.exe и без перезапуска: обновление, о котором не спрашивали
    /// (все изменения про выключенные функции), применяется при обычном выходе из Screeny.
    /// </summary>
    void ApplySilentlyAfterExit(UpdateCandidate update);
}

/// <summary>Источник «ниоткуда»: копия считается необновляемой и ни к чему не обращается.</summary>
public sealed class NoUpdateClient : IUpdateClient
{
    public static readonly NoUpdateClient Instance = new();
    private NoUpdateClient() { }
    public bool CanUpdate => false;
    public string? CurrentVersion => null;
    public Task<UpdateCandidate?> CheckAsync(CancellationToken ct) => Task.FromResult<UpdateCandidate?>(null);
    public Task DownloadAsync(UpdateCandidate update, Action<int>? progress, CancellationToken ct) => Task.CompletedTask;
    public void ApplyAfterExit(UpdateCandidate update, string[] restartArgs) => throw new InvalidOperationException("No update source");
    public void ApplySilentlyAfterExit(UpdateCandidate update) => throw new InvalidOperationException("No update source");
}

/// <summary>Обновления через Velopack: GitHub Releases или (только <c>--dev --update-source</c>) локальная папка.</summary>
public sealed class VelopackUpdateClient : IUpdateClient
{
    public const string RepositoryUrl = "https://github.com/DontIgnore/Screeny";

    private readonly UpdateManager _manager;

    private VelopackUpdateClient(IUpdateSource source) => _manager = new UpdateManager(source);

    public static VelopackUpdateClient ForGithub() => new(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));

    /// <summary>Папка с результатом <c>tools/pack.ps1</c> (<c>releases.win.json</c> и <c>*.nupkg</c>).</summary>
    public static VelopackUpdateClient ForFolder(string folder) => new(new SimpleFileSource(new DirectoryInfo(folder)));

    public bool CanUpdate => _manager.IsInstalled;

    public string? CurrentVersion => _manager.CurrentVersion?.ToString();

    public async Task<UpdateCandidate?> CheckAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info is null) return null;
        var target = info.TargetFullRelease;
        // Промежуточные версии Velopack знает только по цепочке дельт (DeltasToTarget: по дельте на каждую версию после
        // текущей, до целевой включительно; есть не всегда — нужен локальный пакет текущей версии). Без цепочки
        // (или если она не доходит до целевой) промежуточные неизвестны: null, и молча такое обновление не ставится.
        var deltas = info.DeltasToTarget ?? [];
        IReadOnlyList<string?>? earlier = deltas.Any(d => d.Version == target.Version)
            ? deltas
                .Where(d => d.Version < target.Version)
                .GroupBy(d => d.Version).Select(g => g.First())
                .OrderByDescending(d => d.Version)
                .Select(d => d.NotesMarkdown)
                .ToList()
            : null;
        return new UpdateCandidate(target.Version.ToString(), target.NotesMarkdown, info, earlier);
    }

    public Task DownloadAsync(UpdateCandidate update, Action<int>? progress, CancellationToken ct)
        => _manager.DownloadUpdatesAsync((UpdateInfo)update.Handle!, progress, ct);

    public void ApplyAfterExit(UpdateCandidate update, string[] restartArgs)
        => _manager.WaitExitThenApplyUpdates(((UpdateInfo)update.Handle!).TargetFullRelease, silent: false, restart: true, restartArgs);

    public void ApplySilentlyAfterExit(UpdateCandidate update)
        => _manager.WaitExitThenApplyUpdates(((UpdateInfo)update.Handle!).TargetFullRelease, silent: true, restart: false);
}

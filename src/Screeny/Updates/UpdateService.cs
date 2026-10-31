using Screeny.Logging;

namespace Screeny.Updates;

public enum UpdateCheckKind { Automatic, Manual }

public enum UpdateSourceKind { None, Github, Folder }

/// <summary>Check result.</summary>
public abstract record UpdateCheckResult
{
    /// <summary>No newer.</summary>
    public sealed record UpToDate(string? CurrentVersion) : UpdateCheckResult;
    /// <summary>New version worth showing.</summary>
    public sealed record Available(UpdateCandidate Update) : UpdateCheckResult;
    /// <summary>New version exists but user skipped it (only automatic check).</summary>
    public sealed record Skipped(UpdateCandidate Update) : UpdateCheckResult;
    /// <summary>Copy can't update (not built via installer or portable).</summary>
    public sealed record NotUpdatable : UpdateCheckResult;
    /// <summary>Check already running.</summary>
    public sealed record Busy : UpdateCheckResult;
    public sealed record Failed(Exception Error) : UpdateCheckResult;
}

/// <summary>
/// Update check: at startup (via <see cref="StartupDelay"/>) then every <see cref="Interval"/>, while
/// “Check for updates” setting enabled; manual — from tray menu. Automatic check silent on errors
/// (only log) and skipped versions; manual shows everything.
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
    // One UpdateManager can't download two updates at once (silent background and “Install” from window): downloads sequential.
    private readonly SemaphoreSlim _downloadLock = new(1, 1);
    private string? _downloadedVersion;

    /// <summary>Automatic check found version user didn't skip.</summary>
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
    /// Where to get updates: <c>--update-source</c> (only dev) — folder; normal — GitHub; test instance
    /// without folder — nowhere (neither automatic nor manual check goes to network).
    /// </summary>
    public static UpdateSourceKind ChooseSource(bool isDev, string? localSource)
        => localSource is not null ? UpdateSourceKind.Folder : isDev ? UpdateSourceKind.None : UpdateSourceKind.Github;

    /// <summary>
    /// Background check possible at all: copy updatable, test instance only with explicit local
    /// source (<c>--dev --update-source</c>), so dev runs never go to network.
    /// </summary>
    public static bool AutomaticAllowed(bool isDev, bool hasLocalSource, bool canUpdate)
        => canUpdate && (!isDev || hasLocalSource);

    /// <summary>Loop: pause after startup, then check daily. Setting read before each check.</summary>
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
        catch (OperationCanceledException) { /* exit */ }
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
            // No network, repo unavailable, GitHub API limit — log; manual check shows message.
            Log.Error($"Update check ({kind}) failed", ex);
            return new UpdateCheckResult.Failed(ex);
        }
        finally { _checking = false; }
    }

    /// <summary>
    /// Download update. Downloads sequential: second waits first. Already downloaded version (e.g., by silent download)
    /// not downloaded again — immediately 100%.
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

    /// <summary>“Skip this version”: automatic check won't remind about it (next one will).</summary>
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

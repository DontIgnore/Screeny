using Velopack;
using Velopack.Sources;

namespace Screeny.Updates;

/// <summary>Found update: version, changelog (markdown) and Velopack object for download.</summary>
/// <param name="EarlierNotes">Intermediate version changelogs (newer first), if source knows all versions
/// between current and target: empty list — target immediately after current; null — intermediates unknown.</param>
public sealed record UpdateCandidate(string Version, string? NotesMarkdown, object? Handle = null, IReadOnlyList<string?>? EarlierNotes = null)
{
    /// <summary>Changelogs of all versions newest to oldest: target, then intermediates.</summary>
    public IEnumerable<string?> AllNotes => EarlierNotes is null ? [NotesMarkdown] : [NotesMarkdown, .. EarlierNotes];
}

/// <summary>Update source; interface to test <see cref="UpdateService"/> without network and Velopack.</summary>
public interface IUpdateClient
{
    /// <summary>Copy can update: installed via Setup or run from Velopack portable build.</summary>
    bool CanUpdate { get; }
    /// <summary>Current version (from Velopack package); null for copy not built via <c>tools/pack.ps1</c>.</summary>
    string? CurrentVersion { get; }
    /// <summary>null — no newer. Network and source errors — exception.</summary>
    Task<UpdateCandidate?> CheckAsync(CancellationToken ct);
    Task DownloadAsync(UpdateCandidate update, Action<int>? progress, CancellationToken ct);
    /// <summary>
    /// Launches Update.exe, which waits for app exit (up to 60s), applies update and restarts Screeny
    /// with <paramref name="restartArgs"/>. App must exit right after call.
    /// </summary>
    void ApplyAfterExit(UpdateCandidate update, string[] restartArgs);
    /// <summary>
    /// Like <see cref="ApplyAfterExit"/>, but without Update.exe windows and restart: update not asked about
    /// (all changes concern disabled features) applied on normal Screeny exit.
    /// </summary>
    void ApplySilentlyAfterExit(UpdateCandidate update);
}

/// <summary>“Nowhere” source: copy considered non-updatable, contacts nothing.</summary>
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

/// <summary>Updates via Velopack: GitHub Releases or (only <c>--dev --update-source</c>) local folder.</summary>
public sealed class VelopackUpdateClient : IUpdateClient
{
    public const string RepositoryUrl = "https://github.com/DontIgnore/Screeny";

    private readonly UpdateManager _manager;

    private VelopackUpdateClient(IUpdateSource source) => _manager = new UpdateManager(source);

    public static VelopackUpdateClient ForGithub() => new(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));

    /// <summary>Folder with result of <c>tools/pack.ps1</c> (<c>releases.win.json</c> and <c>*.nupkg</c>).</summary>
    public static VelopackUpdateClient ForFolder(string folder) => new(new SimpleFileSource(new DirectoryInfo(folder)));

    public bool CanUpdate => _manager.IsInstalled;

    public string? CurrentVersion => _manager.CurrentVersion?.ToString();

    public async Task<UpdateCandidate?> CheckAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (info is null) return null;
        var target = info.TargetFullRelease;
        // Intermediate versions Velopack knows only via delta chain (DeltasToTarget: one delta per version after
        // current up to target inclusive; not always present — needs local current version package). Without chain
        // (or if doesn't reach target) intermediates unknown: null, and silent such update not installed.
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

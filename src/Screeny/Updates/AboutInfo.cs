using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;

namespace Screeny.Updates;

/// <summary>Version of this copy and About links.</summary>
public static class AppVersion
{
    /// <summary>
    /// Version for display: from Velopack package (also seen by update check); without package (dev, publish) — from
    /// assembly, without <c>+hash</c> of commit. Prerelease suffix (<c>-beta.1</c>) stays.
    /// </summary>
    public static string Choose(string? velopackVersion, string? informationalVersion)
        => !string.IsNullOrWhiteSpace(velopackVersion) ? velopackVersion.Trim() : Strip(informationalVersion) ?? "0.0.0";

    /// <summary><c>0.2.1-beta.1+abc123</c> → <c>0.2.1-beta.1</c>; empty — null.</summary>
    public static string? Strip(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return null;
        var plus = informationalVersion.IndexOf('+');
        var version = (plus >= 0 ? informationalVersion[..plus] : informationalVersion).Trim();
        return version.Length == 0 ? null : version;
    }

    /// <summary>AssemblyInformationalVersion of Screeny assembly (from <c>&lt;Version&gt;</c> in csproj, with <c>+hash</c>).</summary>
    public static string? Informational()
        => typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>Release page of this version on GitHub (“What's new”).</summary>
    public static string ReleasePageUrl(string version) => $"{VelopackUpdateClient.RepositoryUrl}/releases/tag/v{version}";

    public static string LicenseUrl => $"{VelopackUpdateClient.RepositoryUrl}/blob/main/LICENSE";
}

public enum AboutUpdateState { Idle, Checking, UpToDate, Available, Failed, NotUpdatable }

/// <summary>
/// Status string under “Check for updates” button in settings. State stored, not text: text rebuilt
/// on language change.
/// </summary>
/// <param name="Update">Found version (<see cref="AboutUpdateState.Available"/>): “Update” button opens its window.</param>
/// <param name="ReasonKey">Key of clear error reason; null — show <paramref name="ReasonText"/>.</param>
public sealed record AboutUpdateStatus(AboutUpdateState State, UpdateCandidate? Update = null, string? ReasonKey = null, string? ReasonText = null)
{
    public static readonly AboutUpdateStatus Idle = new(AboutUpdateState.Idle);
    public static readonly AboutUpdateStatus Checking = new(AboutUpdateState.Checking);

    /// <summary>Before check: copy that can't update itself (dev, publish) says so immediately.</summary>
    public static AboutUpdateStatus Initial(bool canUpdate) => canUpdate ? Idle : new(AboutUpdateState.NotUpdatable);

    /// <summary>
    /// Manual check result → state. null (no check: exit or update window already open) and
    /// <see cref="UpdateCheckResult.Busy"/> (another check running, its result won't come here) return
    /// <paramref name="previous"/>, so “Checking…” doesn't hang forever.
    /// </summary>
    public static AboutUpdateStatus From(UpdateCheckResult? result, AboutUpdateStatus previous) => result switch
    {
        UpdateCheckResult.Available a => new(AboutUpdateState.Available, a.Update),
        UpdateCheckResult.Skipped s => new(AboutUpdateState.Available, s.Update), // manual check ignores skip
        UpdateCheckResult.UpToDate => new(AboutUpdateState.UpToDate),
        UpdateCheckResult.NotUpdatable => new(AboutUpdateState.NotUpdatable),
        UpdateCheckResult.Failed f => Failed(f.Error),
        _ => previous.State == AboutUpdateState.Checking ? Idle : previous,
    };

    private static AboutUpdateStatus Failed(Exception error)
        => ReasonKeyFor(error) is { } key ? new(AboutUpdateState.Failed, ReasonKey: key) : new(AboutUpdateState.Failed, ReasonText: error.Message);

    /// <summary>
    /// Clear reason: no connection, GitHub rate-limited or unavailable, no response. Else — null (error text).
    /// Searched via InnerException chain: Velopack wraps download errors.
    /// </summary>
    public static string? ReasonKeyFor(Exception error)
    {
        for (var ex = error; ex is not null; ex = ex.InnerException)
        {
            switch (ex)
            {
                case TimeoutException or TaskCanceledException:
                    return "About.ReasonTimeout";
                case HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests }:
                    return "About.ReasonLimit";
                case HttpRequestException { StatusCode: >= HttpStatusCode.InternalServerError }:
                    return "About.ReasonServer";
                case HttpRequestException { StatusCode: null }:
                case SocketException or WebException:
                    return "About.ReasonNetwork";
            }
        }
        return null;
    }
}

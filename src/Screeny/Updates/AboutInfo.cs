using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;

namespace Screeny.Updates;

/// <summary>Версия этой копии и ссылки раздела «О программе».</summary>
public static class AppVersion
{
    /// <summary>
    /// Версия для показа: из пакета Velopack (её же видит проверка обновлений); у копии без пакета (dev, publish) — из
    /// сборки, без <c>+хеш</c> коммита. Суффикс предрелиза (<c>-beta.1</c>) остаётся.
    /// </summary>
    public static string Choose(string? velopackVersion, string? informationalVersion)
        => !string.IsNullOrWhiteSpace(velopackVersion) ? velopackVersion.Trim() : Strip(informationalVersion) ?? "0.0.0";

    /// <summary><c>0.2.1-beta.1+abc123</c> → <c>0.2.1-beta.1</c>; пустая — null.</summary>
    public static string? Strip(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion)) return null;
        var plus = informationalVersion.IndexOf('+');
        var version = (plus >= 0 ? informationalVersion[..plus] : informationalVersion).Trim();
        return version.Length == 0 ? null : version;
    }

    /// <summary>AssemblyInformationalVersion сборки Screeny (из <c>&lt;Version&gt;</c> в csproj, с <c>+хеш</c>).</summary>
    public static string? Informational()
        => typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    /// <summary>Страница релиза этой версии на GitHub («Что нового»).</summary>
    public static string ReleasePageUrl(string version) => $"{VelopackUpdateClient.RepositoryUrl}/releases/tag/v{version}";

    public static string LicenseUrl => $"{VelopackUpdateClient.RepositoryUrl}/blob/main/LICENSE";
}

public enum AboutUpdateState { Idle, Checking, UpToDate, Available, Failed, NotUpdatable }

/// <summary>
/// Строка состояния под кнопкой «Проверить обновления» в настройках. Хранится состояние, а не текст: текст собирается
/// заново при смене языка.
/// </summary>
/// <param name="Update">Найденная версия (<see cref="AboutUpdateState.Available"/>): кнопка «Обновить» открывает её окно.</param>
/// <param name="ReasonKey">Ключ понятной причины ошибки; null — показать <paramref name="ReasonText"/>.</param>
public sealed record AboutUpdateStatus(AboutUpdateState State, UpdateCandidate? Update = null, string? ReasonKey = null, string? ReasonText = null)
{
    public static readonly AboutUpdateStatus Idle = new(AboutUpdateState.Idle);
    public static readonly AboutUpdateStatus Checking = new(AboutUpdateState.Checking);

    /// <summary>До проверки: копия, которая не обновляется сама (dev, publish), сразу говорит об этом.</summary>
    public static AboutUpdateStatus Initial(bool canUpdate) => canUpdate ? Idle : new(AboutUpdateState.NotUpdatable);

    /// <summary>
    /// Итог ручной проверки → состояние. null (проверки не было: выход или окно обновления уже открыто) и
    /// <see cref="UpdateCheckResult.Busy"/> (идёт другая проверка, её итог сюда не придёт) возвращают
    /// <paramref name="previous"/>, чтобы «Проверяю…» не повисло навсегда.
    /// </summary>
    public static AboutUpdateStatus From(UpdateCheckResult? result, AboutUpdateStatus previous) => result switch
    {
        UpdateCheckResult.Available a => new(AboutUpdateState.Available, a.Update),
        UpdateCheckResult.Skipped s => new(AboutUpdateState.Available, s.Update), // ручная проверка пропуск не учитывает
        UpdateCheckResult.UpToDate => new(AboutUpdateState.UpToDate),
        UpdateCheckResult.NotUpdatable => new(AboutUpdateState.NotUpdatable),
        UpdateCheckResult.Failed f => Failed(f.Error),
        _ => previous.State == AboutUpdateState.Checking ? Idle : previous,
    };

    private static AboutUpdateStatus Failed(Exception error)
        => ReasonKeyFor(error) is { } key ? new(AboutUpdateState.Failed, ReasonKey: key) : new(AboutUpdateState.Failed, ReasonText: error.Message);

    /// <summary>
    /// Понятная причина: нет связи, GitHub ограничил запросы или недоступен, нет ответа. Остальное — null (текст ошибки).
    /// Ищется по цепочке InnerException: Velopack заворачивает ошибки загрузки.
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

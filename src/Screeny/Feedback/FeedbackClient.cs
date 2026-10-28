using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Screeny.Feedback;

/// <summary>Итог отправки: окно показывает по нему состояние, текст пользователя при любой ошибке остаётся.</summary>
public abstract record FeedbackResult
{
    /// <summary>201 (или другой 2xx). <paramref name="Id"/> — короткий номер; null, если сервер его не вернул.</summary>
    public sealed record Sent(string? Id) : FeedbackResult;
    /// <summary>400: сервер не принял данные; <paramref name="Message"/> — его объяснение, если есть.</summary>
    public sealed record Invalid(string? Message) : FeedbackResult;
    /// <summary>429: слишком часто. <paramref name="RetryAfter"/> — из заголовка Retry-After, если он есть.</summary>
    public sealed record RateLimited(TimeSpan? RetryAfter) : FeedbackResult;
    /// <summary>5xx.</summary>
    public sealed record ServerError(int Status) : FeedbackResult;
    /// <summary>Другой код ответа (404, 413…).</summary>
    public sealed record Failed(int Status) : FeedbackResult;
    /// <summary>Нет сети, сервер недоступен или не ответил вовремя.</summary>
    public sealed record Offline : FeedbackResult;
    /// <summary>Тестовый экземпляр без <c>--feedback-endpoint</c>: в сеть не ходим.</summary>
    public sealed record NotConfigured : FeedbackResult;
}

/// <summary>Куда отправлять сообщения.</summary>
public static class FeedbackEndpoint
{
    public const string Production = "https://screeny.page/api/feedback";

    /// <summary>
    /// Рабочая копия — всегда <see cref="Production"/>. Тестовая (<c>--dev</c>) — только явный <c>--feedback-endpoint</c>
    /// с абсолютным http(s)-адресом, иначе null: dev-запуск никогда не отправляет в настоящую очередь.
    /// </summary>
    public static Uri? Choose(bool isDev, string? explicitEndpoint)
    {
        if (!isDev) return new Uri(Production);
        return Uri.TryCreate(explicitEndpoint?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;
    }
}

/// <summary><c>POST</c> сообщения и разбор ответа в <see cref="FeedbackResult"/>. В журнал пишутся только коды, не текст.</summary>
public sealed class FeedbackClient(HttpClient http, Uri? endpoint, Func<DateTimeOffset>? now = null)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Отмена через <paramref name="ct"/> (окно закрыли) пробрасывается; истёкшее ожидание — <see cref="FeedbackResult.Offline"/>.</summary>
    public async Task<FeedbackResult> SendAsync(FeedbackRequest request, CancellationToken ct)
    {
        if (endpoint is null) return new FeedbackResult.NotConfigured();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        try
        {
            using var content = new StringContent(request.ToJson(), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(endpoint, content, timeout.Token).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            var result = Map(response, body, (now ?? (() => DateTimeOffset.UtcNow))());
            if (result is not FeedbackResult.Sent) Logging.Log.Error($"Feedback not accepted: HTTP {(int)response.StatusCode}");
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Logging.Log.Error("Feedback send timed out");
            return new FeedbackResult.Offline();
        }
        catch (HttpRequestException ex)
        {
            Logging.Log.Error($"Feedback send failed: {ex.HttpRequestError}");
            return new FeedbackResult.Offline();
        }
    }

    public static FeedbackResult Map(HttpResponseMessage response, string? body, DateTimeOffset now)
    {
        int status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode) return new FeedbackResult.Sent(ReadString(body, "id"));
        if (response.StatusCode == HttpStatusCode.BadRequest) return new FeedbackResult.Invalid(ReadString(body, "error"));
        if (response.StatusCode == HttpStatusCode.TooManyRequests) return new FeedbackResult.RateLimited(RetryAfter(response, now));
        return status >= 500 ? new FeedbackResult.ServerError(status) : new FeedbackResult.Failed(status);
    }

    /// <summary>Retry-After — секунды или дата; прошедшая дата и отрицательное значение — ноль.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header?.Date is { } date) return date > now ? date - now : TimeSpan.Zero;
        return null;
    }

    /// <summary>Строковое поле JSON-объекта; не JSON, не объект, нет поля, пустое — null.</summary>
    public static string? ReadString(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty(name, out var value)
                   && value.ValueKind == JsonValueKind.String
                   && !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!.Trim()
                : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Минуты для «попробуйте через N мин.»: вверх до целой, не меньше одной. null — сервер не сказал, сколько ждать.
    /// </summary>
    public static int? RetryMinutes(TimeSpan? retryAfter)
        => retryAfter is { } t ? Math.Max(1, (int)Math.Ceiling(t.TotalMinutes)) : null;
}

/// <summary>Ссылка «Открыть на GitHub»: форма issue с заполненным заголовком.</summary>
public static class GitHubIssueLink
{
    public const string NewIssueUrl = "https://github.com/DontIgnore/Screeny/issues/new";

    public static string Template(FeedbackKind kind) => kind == FeedbackKind.Bug ? "bug_report.yml" : "feature_request.yml";

    /// <summary>Заголовок кодируется целиком (<c>&amp;</c>, <c>#</c>, пробелы, кириллица); пустой — без параметра.</summary>
    public static string Build(FeedbackKind kind, string? title)
    {
        var url = $"{NewIssueUrl}?template={Template(kind)}";
        var t = title?.Trim();
        return string.IsNullOrEmpty(t) ? url : $"{url}&title={Uri.EscapeDataString(t)}";
    }
}

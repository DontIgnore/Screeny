using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Screeny.Feedback;

/// <summary>Send result: window states it, user text stays on any error.</summary>
public abstract record FeedbackResult
{
    /// <summary>201 (or other 2xx). <paramref name="Id"/> — short number; null if server didn't return it.</summary>
    public sealed record Sent(string? Id) : FeedbackResult;
    /// <summary>400: server rejected data; <paramref name="Message"/> — its explanation if any.</summary>
    public sealed record Invalid(string? Message) : FeedbackResult;
    /// <summary>429: too often. <paramref name="RetryAfter"/> — from Retry-After header if present.</summary>
    public sealed record RateLimited(TimeSpan? RetryAfter) : FeedbackResult;
    /// <summary>5xx.</summary>
    public sealed record ServerError(int Status) : FeedbackResult;
    /// <summary>Other response code (404, 413…).</summary>
    public sealed record Failed(int Status) : FeedbackResult;
    /// <summary>No network, server unavailable or didn't answer in time.</summary>
    public sealed record Offline : FeedbackResult;
    /// <summary>Test instance without <c>--feedback-endpoint</c>: no network calls.</summary>
    public sealed record NotConfigured : FeedbackResult;
}

/// <summary>Where to send messages.</summary>
public static class FeedbackEndpoint
{
    public const string Production = "https://screeny.page/api/feedback";

    /// <summary>
    /// Working copy always <see cref="Production"/>. Test (<c>--dev</c>) — only explicit <c>--feedback-endpoint</c>
    /// with absolute http(s) address, else null: dev launch never sends to real queue.
    /// </summary>
    public static Uri? Choose(bool isDev, string? explicitEndpoint)
    {
        if (!isDev) return new Uri(Production);
        return Uri.TryCreate(explicitEndpoint?.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;
    }
}

/// <summary><c>POST</c> message and response parsing into <see cref="FeedbackResult"/>. Log writes only codes, not text.</summary>
public sealed class FeedbackClient(HttpClient http, Uri? endpoint, Func<DateTimeOffset>? now = null)
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>Cancellation via <paramref name="ct"/> (window closed) propagates; expired wait — <see cref="FeedbackResult.Offline"/>.</summary>
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

    /// <summary>Retry-After — seconds or date; past date and negative — zero.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta < TimeSpan.Zero ? TimeSpan.Zero : delta;
        if (header?.Date is { } date) return date > now ? date - now : TimeSpan.Zero;
        return null;
    }

    /// <summary>String field of JSON object; not JSON, not object, missing field, empty — null.</summary>
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
    /// Minutes for “try again in N minutes”: round up to a whole minute, with a minimum of one. null means the server did not specify a delay.
    /// </summary>
    public static int? RetryMinutes(TimeSpan? retryAfter)
        => retryAfter is { } t ? Math.Max(1, (int)Math.Ceiling(t.TotalMinutes)) : null;
}

/// <summary>Link “Open on GitHub”: issue form with filled title.</summary>
public static class GitHubIssueLink
{
    public const string NewIssueUrl = "https://github.com/DontIgnore/Screeny/issues/new";

    public static string Template(FeedbackKind kind) => kind == FeedbackKind.Bug ? "bug_report.yml" : "feature_request.yml";

    /// <summary>Title encoded entirely (<c>&amp;</c>, <c>#</c>, spaces, Cyrillic); empty — no parameter.</summary>
    public static string Build(FeedbackKind kind, string? title)
    {
        var url = $"{NewIssueUrl}?template={Template(kind)}";
        var t = title?.Trim();
        return string.IsNullOrEmpty(t) ? url : $"{url}&title={Uri.EscapeDataString(t)}";
    }
}

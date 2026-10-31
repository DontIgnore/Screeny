using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Screeny.Feedback;

namespace Screeny.Tests.Feedback;

public class FeedbackRequestTests
{
    private static readonly FeedbackDiagnostics Diag = new("0.2.2", "Windows 11 (10.0.26200, X64)", "ru", 144, 2, "line 1\nline 2");

    [Fact]
    public void Json_HasContractFieldNames_AndLowercaseKind()
    {
        var json = new FeedbackDraft(FeedbackKind.Bug, "Title", "Description!", "me@example.com", Diag).ToRequest("id-1").ToJson();
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        Assert.Equal(["kind", "title", "description", "contact", "diagnostics", "installId"], r.EnumerateObject().Select(p => p.Name));
        Assert.Equal("bug", r.GetProperty("kind").GetString());
        Assert.Equal("id-1", r.GetProperty("installId").GetString());
        var d = r.GetProperty("diagnostics");
        Assert.Equal(["appVersion", "osVersion", "uiLanguage", "dpi", "monitors", "logTail"], d.EnumerateObject().Select(p => p.Name));
        Assert.Equal(144, d.GetProperty("dpi").GetInt32());
        Assert.Equal(2, d.GetProperty("monitors").GetInt32());
        Assert.Equal("line 1\nline 2", d.GetProperty("logTail").GetString());
    }

    [Fact]
    public void Idea_WithoutContactAndDiagnostics_WritesExplicitNulls()
    {
        var json = new FeedbackDraft(FeedbackKind.Idea, "Idea", "Some longer text", "   ", null).ToRequest("x").ToJson();
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("idea", doc.RootElement.GetProperty("kind").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("contact").ValueKind);
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("diagnostics").ValueKind);
    }

    [Fact]
    public void ToRequest_TrimsFields()
    {
        var r = new FeedbackDraft(FeedbackKind.Bug, "  Title \n", "\n Description text  ", " @tg ", null).ToRequest("x");
        Assert.Equal("Title", r.Title);
        Assert.Equal("Description text", r.Description);
        Assert.Equal("@tg", r.Contact);
    }

    [Fact]
    public void Json_KeepsCyrillicReadable_AndEscapesQuotes()
    {
        var json = new FeedbackDraft(FeedbackKind.Bug, "Ошибка \"пина\"", "Описание ошибки", null, null).ToRequest("x").ToJson();
        Assert.Contains("Ошибка \\\"пина\\\"", json);
        Assert.Equal("Ошибка \"пина\"", JsonDocument.Parse(json).RootElement.GetProperty("title").GetString());
    }
}

public class FeedbackRulesTests
{
    [Theory]
    [InlineData("ab", FeedbackFieldError.TooShort)]
    [InlineData("  ab  ", FeedbackFieldError.TooShort)] // edge spaces not counted
    [InlineData("", FeedbackFieldError.TooShort)]
    [InlineData(null, FeedbackFieldError.TooShort)]
    [InlineData("abc", null)]
    public void Title(string? text, FeedbackFieldError? expected) => Assert.Equal(expected, FeedbackRules.CheckTitle(text));

    [Fact]
    public void Title_Boundaries()
    {
        Assert.Null(FeedbackRules.CheckTitle(new string('a', 120)));
        Assert.Equal(FeedbackFieldError.TooLong, FeedbackRules.CheckTitle(new string('a', 121)));
    }

    [Fact]
    public void Description_Boundaries()
    {
        Assert.Equal(FeedbackFieldError.TooShort, FeedbackRules.CheckDescription(new string('a', 9)));
        Assert.Null(FeedbackRules.CheckDescription(new string('a', 10)));
        Assert.Null(FeedbackRules.CheckDescription(new string('a', 5000)));
        Assert.Equal(FeedbackFieldError.TooLong, FeedbackRules.CheckDescription(new string('a', 5001)));
    }

    [Fact]
    public void Contact_IsOptional_ButLimited()
    {
        Assert.Null(FeedbackRules.CheckContact(null));
        Assert.Null(FeedbackRules.CheckContact(""));
        Assert.Null(FeedbackRules.CheckContact(new string('a', 200)));
        Assert.Equal(FeedbackFieldError.TooLong, FeedbackRules.CheckContact(new string('a', 201)));
    }

    [Fact]
    public void IsValid_NeedsAllFields()
    {
        Assert.True(FeedbackRules.IsValid("Title", "Long enough", null));
        Assert.False(FeedbackRules.IsValid("Title", "short", null));
        Assert.False(FeedbackRules.IsValid("Title", "Long enough", new string('a', 201)));
    }
}

public class LogScrubberTests
{
    private static readonly ScrubContext Ivan = new(@"C:\Users\Ivan.Petrov", "Ivan.Petrov");

    [Theory]
    [InlineData(@"Settings read failed: C:\Users\Ivan.Petrov\AppData\Roaming\Screeny\settings.json",
                @"Settings read failed: %USERPROFILE%\AppData\Roaming\Screeny\settings.json")]
    [InlineData(@"History list failed: c:\users\ivan.petrov\Pictures", @"History list failed: %USERPROFILE%\Pictures")]
    [InlineData("Open link failed: file:///C:/Users/Ivan.Petrov/x.png", "Open link failed: file:///%USERPROFILE%/x.png")]
    [InlineData(@"{""path"":""C:\\Users\\Ivan.Petrov\\x""}", @"{""path"":""%USERPROFILE%\\x""}")]
    public void UserProfile_IsReplaced(string line, string expected) => Assert.Equal(expected, LogScrubber.Scrub(line, Ivan));

    [Theory]
    [InlineData(@"Module temp folder not deleted: C:\Users\IVANPE~1\AppData\Local\Temp\x", @"Module temp folder not deleted: C:\Users\<user>\AppData\Local\Temp\x")]
    [InlineData(@"Other profile: D:\Users\someone\file", @"Other profile: D:\Users\<user>\file")]
    public void OtherUsersFolders_AreReplaced(string line, string expected) => Assert.Equal(expected, LogScrubber.Scrub(line, Ivan));

    [Fact]
    public void UserNameAsPathSegment_IsReplaced()
        => Assert.Equal(@"History prune failed: D:\Shots\<user>\2026\shot.png",
            LogScrubber.Scrub(@"History prune failed: D:\Shots\Ivan.Petrov\2026\shot.png", Ivan));

    [Fact]
    public void UserNameInsideAWord_IsKept()
        => Assert.Equal(@"D:\Shots\Ivan.PetrovArchive\a.png", LogScrubber.Scrub(@"D:\Shots\Ivan.PetrovArchive\a.png", Ivan));

    [Fact]
    public void RecognizedTextLine_IsDropped()
        => Assert.Equal(LogScrubber.RemovedLine,
            LogScrubber.Scrub("2026-10-28T10:00:00 Dev text mode: layout shown, 12 words: Secret | password 1234", Ivan));

    [Fact]
    public void LongLines_AreCut()
    {
        var line = LogScrubber.Scrub(new string('x', 2000), Ivan);
        Assert.Equal(LogScrubber.MaxLineLength + 1, line.Length);
        Assert.EndsWith("…", line);
    }

    [Fact]
    public void Tail_KeepsLastLines_InOrder()
    {
        var lines = Enumerable.Range(1, 250).Select(i => $"line {i}");
        var tail = LogScrubber.Tail(lines, Ivan).Split('\n');
        Assert.Equal(LogScrubber.TailLines, tail.Length);
        Assert.Equal("line 151", tail[0]);
        Assert.Equal("line 250", tail[^1]);
    }

    [Fact]
    public void ReadTail_ReadsFileOpenForWriting_AndScrubs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ScreenyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "screeny.log");
        try
        {
            using (var writer = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
            using (var w = new StreamWriter(writer))
            {
                for (int i = 0; i < 150; i++) w.Write($"entry {i}\r\n");
                w.Write(@"last C:\Users\Ivan.Petrov\a.txt" + "\r\n");
                w.Flush();
                var tail = LogScrubber.ReadTail(path, Ivan).Split('\n');
                Assert.Equal(100, tail.Length);
                Assert.Equal(@"last %USERPROFILE%\a.txt", tail[^1]);
            }
            Assert.Equal("", LogScrubber.ReadTail(Path.Combine(dir, "missing.log"), Ivan));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void ReadTail_BigFile_ReadsOnlyTheEnd()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ScreenyTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "screeny.log");
        try
        {
            File.WriteAllLines(path, Enumerable.Range(0, 20_000).Select(i => $"entry {i} {new string('.', 40)}"));
            var tail = LogScrubber.ReadTail(path, Ivan).Split('\n');
            Assert.Equal(100, tail.Length);
            Assert.StartsWith("entry 19900 ", tail[0]);
            Assert.StartsWith("entry 19999 ", tail[^1]);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }
}

public class FeedbackClientTests
{
    private static readonly FeedbackRequest Request = new FeedbackDraft(FeedbackKind.Bug, "Title", "Description", null, null).ToRequest("install");
    private static readonly Uri Endpoint = new("http://localhost:5199/api/feedback");
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        public string? LastBody;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return await respond(request);
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode status, string? json = null, Action<HttpResponseMessage>? setup = null)
    {
        var r = new HttpResponseMessage(status);
        if (json is not null) r.Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        setup?.Invoke(r);
        return r;
    }

    private static async Task<(FeedbackResult Result, FakeHandler Handler)> Send(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
    {
        var handler = new FakeHandler(respond);
        using var http = new HttpClient(handler);
        return (await new FeedbackClient(http, Endpoint, () => Now).SendAsync(Request, CancellationToken.None), handler);
    }

    [Fact]
    public async Task Created_ReturnsId_AndPostsJson()
    {
        var (result, handler) = await Send(_ => Task.FromResult(Response(HttpStatusCode.Created, """{"id":"K7Q2M"}""")));
        Assert.Equal(new FeedbackResult.Sent("K7Q2M"), result);
        Assert.Equal(HttpMethod.Post, handler.Last!.Method);
        Assert.Equal(Endpoint, handler.Last.RequestUri);
        Assert.Equal("application/json", handler.Last.Content!.Headers.ContentType!.MediaType);
        Assert.Equal(Request.ToJson(), handler.LastBody);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not json")]
    [InlineData("""{"id":42}""")]
    [InlineData("""[]""")]
    public async Task Created_WithoutReadableId_IsStillSent(string? body)
        => Assert.Equal(new FeedbackResult.Sent(null), (await Send(_ => Task.FromResult(Response(HttpStatusCode.Created, body)))).Result);

    [Fact]
    public async Task BadRequest_CarriesServerMessage()
        => Assert.Equal(new FeedbackResult.Invalid("title too short"),
            (await Send(_ => Task.FromResult(Response(HttpStatusCode.BadRequest, """{"error":"title too short"}""")))).Result);

    [Fact]
    public async Task BadRequest_WithoutMessage()
        => Assert.Equal(new FeedbackResult.Invalid(null), (await Send(_ => Task.FromResult(Response(HttpStatusCode.BadRequest, "oops")))).Result);

    [Fact]
    public async Task TooManyRequests_RetryAfterSeconds()
        => Assert.Equal(new FeedbackResult.RateLimited(TimeSpan.FromSeconds(600)),
            (await Send(_ => Task.FromResult(Response(HttpStatusCode.TooManyRequests, null,
                r => r.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(600)))))).Result);

    [Fact]
    public async Task TooManyRequests_RetryAfterDate()
        => Assert.Equal(new FeedbackResult.RateLimited(TimeSpan.FromMinutes(5)),
            (await Send(_ => Task.FromResult(Response(HttpStatusCode.TooManyRequests, null,
                r => r.Headers.RetryAfter = new RetryConditionHeaderValue(Now.AddMinutes(5)))))).Result);

    [Fact]
    public async Task TooManyRequests_WithoutHeader()
        => Assert.Equal(new FeedbackResult.RateLimited(null), (await Send(_ => Task.FromResult(Response(HttpStatusCode.TooManyRequests)))).Result);

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    public async Task ServerErrors(int status)
        => Assert.Equal(new FeedbackResult.ServerError(status), (await Send(_ => Task.FromResult(Response((HttpStatusCode)status)))).Result);

    [Fact]
    public async Task OtherClientErrors_AreGenericFailures()
        => Assert.Equal(new FeedbackResult.Failed(404), (await Send(_ => Task.FromResult(Response(HttpStatusCode.NotFound)))).Result);

    [Fact]
    public async Task NetworkError_IsOffline()
        => Assert.IsType<FeedbackResult.Offline>((await Send(_ => throw new HttpRequestException("No such host is known."))).Result);

    [Fact]
    public async Task Timeout_IsOffline()
    {
        // No response: client timeout (not user cancel) — “no connection”.
        var handler = new FakeHandler(_ => throw new TaskCanceledException("The request was canceled due to the configured timeout."));
        using var http = new HttpClient(handler);
        Assert.IsType<FeedbackResult.Offline>(await new FeedbackClient(http, Endpoint).SendAsync(Request, CancellationToken.None));
    }

    [Fact]
    public async Task UserCancel_IsRethrown()
    {
        using var cts = new CancellationTokenSource();
        var handler = new FakeHandler(async _ => { cts.Cancel(); await Task.Delay(Timeout.Infinite, cts.Token); return Response(HttpStatusCode.Created); });
        using var http = new HttpClient(handler);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FeedbackClient(http, Endpoint).SendAsync(Request, cts.Token));
    }

    [Fact]
    public async Task NoEndpoint_IsNotConfigured_AndMakesNoRequest()
    {
        var handler = new FakeHandler(_ => throw new InvalidOperationException("must not be called"));
        using var http = new HttpClient(handler);
        Assert.IsType<FeedbackResult.NotConfigured>(await new FeedbackClient(http, null).SendAsync(Request, CancellationToken.None));
        Assert.Null(handler.Last);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0.0, 1)]
    [InlineData(1.0, 1)]
    [InlineData(60.0, 1)]
    [InlineData(61.0, 2)]
    [InlineData(840.0, 14)]
    public void RetryMinutes_RoundsUp(double? seconds, int? expected)
        => Assert.Equal(expected, FeedbackClient.RetryMinutes(seconds is { } s ? TimeSpan.FromSeconds(s) : null));
}

public class FeedbackEndpointTests
{
    [Fact]
    public void Production_AlwaysUsesScreenyPage()
    {
        Assert.Equal(new Uri("https://screeny.page/api/feedback"), FeedbackEndpoint.Choose(isDev: false, null));
        Assert.Equal(new Uri(FeedbackEndpoint.Production), FeedbackEndpoint.Choose(isDev: false, "http://localhost:1/x"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/path")]
    [InlineData("ftp://example.test/feedback")]
    [InlineData(@"C:\feedback")]
    public void Dev_WithoutUsableEndpoint_NeverCallsProduction(string? endpoint)
        => Assert.Null(FeedbackEndpoint.Choose(isDev: true, endpoint));

    [Theory]
    [InlineData("http://localhost:5199/api/feedback")]
    [InlineData("https://staging.screeny.page/api/feedback")]
    public void Dev_WithExplicitEndpoint_UsesIt(string endpoint)
        => Assert.Equal(new Uri(endpoint), FeedbackEndpoint.Choose(isDev: true, endpoint));

    [Fact]
    public void StartupArgs_EndpointOnlyWithDev()
    {
        Assert.Null(StartupArgs.Parse(["--feedback-endpoint", "http://localhost:1/x"]).FeedbackEndpoint);
        Assert.Equal("http://localhost:1/x", StartupArgs.Parse(["--dev", "--feedback-endpoint", "http://localhost:1/x"]).FeedbackEndpoint);
        Assert.Equal("http://localhost:1/x", StartupArgs.Parse(["--feedback-endpoint", "http://localhost:1/x"], "1").FeedbackEndpoint);
        Assert.Null(StartupArgs.Parse(["--dev", "--feedback-endpoint", "  "]).FeedbackEndpoint);
        Assert.Null(StartupArgs.Parse(["--dev", "--feedback-endpoint"]).FeedbackEndpoint);
    }

    [Fact]
    public void StartupArgs_FeedbackWindow_KeepsScenarioOnlyWithDev()
    {
        var dev = StartupArgs.Parse(["--dev", "--feedback", "IDEA", "--scenario", "sent"]);
        Assert.Equal("idea", dev.Feedback);
        Assert.Equal("sent", dev.Scenario);
        var prod = StartupArgs.Parse(["--feedback", "bug", "--scenario", "sent"]);
        Assert.Equal("bug", prod.Feedback);
        Assert.Null(prod.Scenario);
        Assert.Null(StartupArgs.Parse(["--feedback", "other"]).Feedback);
    }
}

public class GitHubIssueLinkTests
{
    [Fact]
    public void Bug_UsesBugTemplate_AndEncodesTitle()
        => Assert.Equal("https://github.com/DontIgnore/Screeny/issues/new?template=bug_report.yml&title=Pin%20%26%20zoom%3A%20100%25%3F%20%232",
            GitHubIssueLink.Build(FeedbackKind.Bug, "  Pin & zoom: 100%? #2 "));

    [Fact]
    public void Idea_UsesFeatureTemplate_AndEncodesCyrillic()
        => Assert.Equal("https://github.com/DontIgnore/Screeny/issues/new?template=feature_request.yml&title=%D0%A0%D0%B0%D0%B7%D0%BC%D1%8B%D1%82%D0%B8%D0%B5",
            GitHubIssueLink.Build(FeedbackKind.Idea, "Размытие"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyTitle_IsLeftOut(string? title)
        => Assert.Equal("https://github.com/DontIgnore/Screeny/issues/new?template=bug_report.yml", GitHubIssueLink.Build(FeedbackKind.Bug, title));
}

public class FeedbackDiagnosticsTests
{
    [Theory]
    [InlineData(10, 0, 26200, "Windows 11 (10.0.26200, X64)")]
    [InlineData(10, 0, 19045, "Windows 10 (10.0.19045, X64)")]
    public void OsVersion_NamesWindowsRelease(int major, int minor, int build, string expected)
        => Assert.Equal(expected, FeedbackDiagnosticsCollector.OsVersion(new Version(major, minor, build), "X64"));
}

/// <summary>Send result → user text (test strings Russian, see TestCulture).</summary>
public class FeedbackMessagesTests
{
    [Fact]
    public void EachFailure_HasItsOwnFriendlyText()
    {
        var texts = new FeedbackResult[]
        {
            new FeedbackResult.Offline(), new FeedbackResult.RateLimited(null), new FeedbackResult.RateLimited(TimeSpan.FromMinutes(14)),
            new FeedbackResult.Invalid("bad title"), new FeedbackResult.Invalid(null), new FeedbackResult.ServerError(503),
            new FeedbackResult.NotConfigured(),
        }.Select(FeedbackWindow.ErrorMessage).ToList();
        Assert.All(texts, t => Assert.False(string.IsNullOrWhiteSpace(t)));
        Assert.Equal(texts.Count, texts.Distinct().Count());
        Assert.Contains("14 мин.", texts[2]);
        Assert.Contains("bad title", texts[3]);
        Assert.Contains("503", texts[5]);
        Assert.Contains("--feedback-endpoint", texts[6]);
    }

    [Fact]
    public void Sent_IsNotAnError() => Assert.Null(FeedbackWindow.ErrorMessage(new FeedbackResult.Sent("abc")));
}

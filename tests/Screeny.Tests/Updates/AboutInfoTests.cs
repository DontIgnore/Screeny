using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Screeny.Updates;

namespace Screeny.Tests.Updates;

public class AboutInfoTests
{
    [Theory]
    [InlineData("0.2.1+1e62d8baae0c88564ca0722c9895b58a21840242", "0.2.1")]
    [InlineData("0.2.1-beta.1+abc123", "0.2.1-beta.1")]
    [InlineData("0.2.1-rc.2", "0.2.1-rc.2")]
    [InlineData("0.2.1", "0.2.1")]
    [InlineData(" 0.2.1 ", "0.2.1")]
    [InlineData("+abc", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void Strip_DropsCommitHash_KeepsPrerelease(string? informational, string? expected)
        => Assert.Equal(expected, AppVersion.Strip(informational));

    [Fact]
    public void Choose_PrefersVelopackVersion_ThenAssembly()
    {
        Assert.Equal("0.2.1", AppVersion.Choose("0.2.1", "0.2.0+abc"));
        Assert.Equal("0.2.0", AppVersion.Choose(null, "0.2.0+abc"));
        Assert.Equal("0.2.0-beta.1", AppVersion.Choose("", "0.2.0-beta.1+abc"));
        Assert.Equal("0.0.0", AppVersion.Choose(null, null));
    }

    [Fact]
    public void Informational_OfThisBuild_StripsToCsprojVersion()
    {
        var version = AppVersion.Strip(AppVersion.Informational());
        Assert.NotNull(version);
        Assert.DoesNotContain('+', version);
        Assert.Equal(typeof(App).Assembly.GetName().Version!.ToString(3), version!.Split('-')[0]);
    }

    [Fact]
    public void Links_PointToTheRepository()
    {
        Assert.Equal("https://github.com/DontIgnore/Screeny/releases/tag/v0.2.1", AppVersion.ReleasePageUrl("0.2.1"));
        Assert.Equal("https://github.com/DontIgnore/Screeny/blob/main/LICENSE", AppVersion.LicenseUrl);
    }

    private static readonly UpdateCandidate Next = new("0.2.1", "notes");

    [Fact]
    public void Initial_NonUpdatableCopy_SaysSoRightAway()
    {
        Assert.Equal(AboutUpdateState.Idle, AboutUpdateStatus.Initial(canUpdate: true).State);
        Assert.Equal(AboutUpdateState.NotUpdatable, AboutUpdateStatus.Initial(canUpdate: false).State);
    }

    [Fact]
    public void From_MapsEveryCheckResult()
    {
        var checking = AboutUpdateStatus.Checking;
        var available = AboutUpdateStatus.From(new UpdateCheckResult.Available(Next), checking);
        Assert.Equal(AboutUpdateState.Available, available.State);
        Assert.Same(Next, available.Update);
        Assert.Equal(AboutUpdateState.Available, AboutUpdateStatus.From(new UpdateCheckResult.Skipped(Next), checking).State);
        Assert.Equal(AboutUpdateState.UpToDate, AboutUpdateStatus.From(new UpdateCheckResult.UpToDate("0.2.1"), checking).State);
        Assert.Equal(AboutUpdateState.NotUpdatable, AboutUpdateStatus.From(new UpdateCheckResult.NotUpdatable(), checking).State);

        var failed = AboutUpdateStatus.From(new UpdateCheckResult.Failed(new HttpRequestException("No such host is known.")), checking);
        Assert.Equal(AboutUpdateState.Failed, failed.State);
        Assert.Equal("About.ReasonNetwork", failed.ReasonKey);
    }

    [Fact]
    public void From_NoResultOrBusy_KeepsPreviousState_ButNeverStaysChecking()
    {
        var upToDate = new AboutUpdateStatus(AboutUpdateState.UpToDate);
        Assert.Same(upToDate, AboutUpdateStatus.From(null, upToDate));
        Assert.Same(upToDate, AboutUpdateStatus.From(new UpdateCheckResult.Busy(), upToDate));
        Assert.Equal(AboutUpdateState.Idle, AboutUpdateStatus.From(new UpdateCheckResult.Busy(), AboutUpdateStatus.Checking).State);
        Assert.Equal(AboutUpdateState.Idle, AboutUpdateStatus.From(null, AboutUpdateStatus.Checking).State);
    }

    private static Exception Error(string kind) => kind switch
    {
        "no-host" => new HttpRequestException("No such host is known.", new SocketException(11001)),
        "403" => new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden),
        "429" => new HttpRequestException("Too many", null, HttpStatusCode.TooManyRequests),
        "502" => new HttpRequestException("Bad gateway", null, HttpStatusCode.BadGateway),
        "http-timeout" => new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout"),
        "wrapped-timeout" => new InvalidOperationException("wrapped", new TimeoutException()),
        "404" => new HttpRequestException("Not found", null, HttpStatusCode.NotFound),
        _ => new InvalidDataException("releases.win.json is damaged"),
    };

    [Theory]
    [InlineData("no-host", "About.ReasonNetwork")]
    [InlineData("403", "About.ReasonLimit")]
    [InlineData("429", "About.ReasonLimit")]
    [InlineData("502", "About.ReasonServer")]
    [InlineData("http-timeout", "About.ReasonTimeout")]
    [InlineData("wrapped-timeout", "About.ReasonTimeout")]
    [InlineData("404", null)]
    [InlineData("damaged", null)]
    public void ReasonKeyFor_GivesFriendlyReason_OrNullForTheErrorText(string kind, string? expected)
        => Assert.Equal(expected, AboutUpdateStatus.ReasonKeyFor(Error(kind)));

    [Fact]
    public void Failed_WithoutKnownReason_KeepsTheErrorText()
    {
        var status = AboutUpdateStatus.From(new UpdateCheckResult.Failed(new InvalidDataException("releases.win.json is damaged")), AboutUpdateStatus.Checking);
        Assert.Null(status.ReasonKey);
        Assert.Equal("releases.win.json is damaged", status.ReasonText);
    }
}

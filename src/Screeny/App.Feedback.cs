using System.Net.Http;
using System.Windows.Input;
using Screeny.Feedback;

namespace Screeny;

/// <summary>
/// Error messages and suggestions: tray items, links in About and <see cref="FeedbackWindow"/> window.
/// Working copy sends to screeny.page, test copy — only to <c>--feedback-endpoint</c> (<see cref="FeedbackEndpoint.Choose"/>).
/// </summary>
public partial class App
{
    private FeedbackWindow? _feedbackWindow;
    private ICommand? _reportBugCommand, _suggestIdeaCommand;
    private HttpClient? _feedbackHttp;

    /// <summary>Tray item “Report a bug…”</summary>
    public ICommand ReportBugCommand => _reportBugCommand ??= new RelayCommand(() => ShowFeedback(FeedbackKind.Bug));

    /// <summary>Tray item “Suggest an improvement…”</summary>
    public ICommand SuggestIdeaCommand => _suggestIdeaCommand ??= new RelayCommand(() => ShowFeedback(FeedbackKind.Idea));

    /// <summary>One window for whole app: repeated call brings open window forward (its text is not lost).</summary>
    public void ShowFeedback(FeedbackKind kind, string? devScenario = null)
    {
        if (IsShuttingDown) return;
        if (_feedbackWindow is not null) { _feedbackWindow.Activate(); return; }
        var window = new FeedbackWindow(kind, SendFeedbackAsync, () => FeedbackDiagnosticsCollector.Collect(CurrentVersion, _feedbackWindow));
        if (IsDev) window.ApplyDevScenario(devScenario);
        window.Closed += (_, _) => _feedbackWindow = null;
        _feedbackWindow = window;
        window.Show();
        window.Activate();
    }

    /// <summary>
    /// Without address (dev without <c>--feedback-endpoint</c>) — immediately “not set”, installId not created, settings not
    /// written. Otherwise installId created on first send and saved.
    /// </summary>
    private Task<FeedbackResult> SendFeedbackAsync(FeedbackDraft draft, CancellationToken ct)
    {
        if (FeedbackEndpoint.Choose(IsDev, Args.FeedbackEndpoint) is not { } endpoint)
            return Task.FromResult<FeedbackResult>(new FeedbackResult.NotConfigured());
        _feedbackHttp ??= CreateFeedbackHttp();
        return new FeedbackClient(_feedbackHttp, endpoint).SendAsync(draft.ToRequest(EnsureInstallId()), ct);
    }

    private HttpClient CreateFeedbackHttp()
    {
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // waiting is bounded by FeedbackClient
        http.DefaultRequestHeaders.UserAgent.TryParseAdd($"Screeny/{CurrentVersion}");
        return http;
    }

    /// <summary>installId from settings; on first send — new random GUID, saved immediately.</summary>
    private string EnsureInstallId()
    {
        if (Settings.InstallId is { } id) return id;
        Settings.InstallId = Guid.NewGuid().ToString("D");
        SaveSettings();
        return Settings.InstallId;
    }

    private void DisposeFeedback()
    {
        _feedbackWindow?.Close();
        _feedbackHttp?.Dispose();
    }
}

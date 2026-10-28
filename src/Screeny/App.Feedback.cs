using System.Net.Http;
using System.Windows.Input;
using Screeny.Feedback;

namespace Screeny;

/// <summary>
/// Сообщения об ошибках и предложения: пункты трея, ссылки в «О программе» и окно <see cref="FeedbackWindow"/>.
/// Рабочая копия отправляет на screeny.page, тестовая — только на <c>--feedback-endpoint</c> (<see cref="FeedbackEndpoint.Choose"/>).
/// </summary>
public partial class App
{
    private FeedbackWindow? _feedbackWindow;
    private ICommand? _reportBugCommand, _suggestIdeaCommand;
    private HttpClient? _feedbackHttp;

    /// <summary>Пункт трея «Сообщить об ошибке…».</summary>
    public ICommand ReportBugCommand => _reportBugCommand ??= new RelayCommand(() => ShowFeedback(FeedbackKind.Bug));

    /// <summary>Пункт трея «Предложить улучшение…».</summary>
    public ICommand SuggestIdeaCommand => _suggestIdeaCommand ??= new RelayCommand(() => ShowFeedback(FeedbackKind.Idea));

    /// <summary>Одно окно на всё приложение: повторный вызов выводит открытое вперёд (его текст не теряется).</summary>
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
    /// Без адреса (dev без <c>--feedback-endpoint</c>) — сразу «не задан», installId при этом не создаётся и настройки не
    /// пишутся. Иначе installId создаётся при первой отправке и сохраняется.
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
        var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; // ожидание ограничивает FeedbackClient
        http.DefaultRequestHeaders.UserAgent.TryParseAdd($"Screeny/{CurrentVersion}");
        return http;
    }

    /// <summary>installId из настроек; при первой отправке — новый случайный GUID, сразу сохраняется.</summary>
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

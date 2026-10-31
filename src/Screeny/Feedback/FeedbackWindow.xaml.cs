using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Screeny.Localization;
using Screeny.Logging;
using Screeny.Native;
using Screeny.Themes;

namespace Screeny.Feedback;

/// <summary>
/// Error message or suggestion, without account: title, description, optional contact and (for error)
/// technical details viewable before send. Validation errors under fields, after first attempt
/// to send. On failure text stays in fields. Code-built texts rebuild on language change.
/// </summary>
public partial class FeedbackWindow : ChromeWindow
{
    /// <summary>Send (App: address, installId, HTTP). Token cancel — window closed during send.</summary>
    public delegate Task<FeedbackResult> SendHandler(FeedbackDraft draft, CancellationToken ct);

    private SendHandler _send;
    private readonly Func<FeedbackDiagnostics> _collectDiagnostics;
    private FeedbackDiagnostics? _diagnostics;
    private CancellationTokenSource? _sending;
    private FeedbackResult? _result;
    private bool _validate;
    private bool _detailsShown;

    public FeedbackKind Kind { get; }

    public FeedbackWindow(FeedbackKind kind, SendHandler send, Func<FeedbackDiagnostics> collectDiagnostics)
    {
        InitializeComponent();
        Kind = kind;
        _send = send;
        _collectDiagnostics = collectDiagnostics;
        NaturalMaxHeight = 760;
        DetailsPanel.Visibility = kind == FeedbackKind.Bug ? Visibility.Visible : Visibility.Collapsed;
        Render();
        Loc.Changed += Render;
        Closed += (_, _) => Loc.Changed -= Render;
        Loaded += (_, _) => TitleInput.Focus();
        ProgressTrack.SizeChanged += (_, _) => { if (SendingPanel.IsVisible) StartProgress(); };
    }

    /// <summary>Technical details: collected once, shown and sent same.</summary>
    private FeedbackDiagnostics Diagnostics => _diagnostics ??= _collectDiagnostics();

    private string Key(string bug, string idea) => Kind == FeedbackKind.Bug ? bug : idea;

    /// <summary>Everything built in code: titles, hints, errors, send result.</summary>
    private void Render()
    {
        Title = Loc.T(Key("Feedback.BugTitle", "Feedback.IdeaTitle"));
        HeadingText.Text = Title;
        IntroText.Text = Loc.T(Key("Feedback.BugIntro", "Feedback.IdeaIntro"));
        TitlePlaceholder.Text = Loc.T(Key("Feedback.BugTitlePlaceholder", "Feedback.IdeaTitlePlaceholder"));
        DescriptionPlaceholder.Text = Loc.T(Key("Feedback.BugDescriptionPlaceholder", "Feedback.IdeaDescriptionPlaceholder"));
        DetailsToggleText.Text = Loc.T(_detailsShown ? "Feedback.HideDetails" : "Feedback.ShowDetails");
        if (_detailsShown) DetailsPreview.Text = PreviewText(Diagnostics);
        UpdatePlaceholders();
        ShowValidation();
        ShowResult();
    }

    private void UpdatePlaceholders()
    {
        TitlePlaceholder.Visibility = TitleInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        DescriptionPlaceholder.Visibility = DescriptionInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ContactPlaceholder.Visibility = ContactInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnFieldChanged(object sender, TextChangedEventArgs e)
    {
        UpdatePlaceholders();
        if (_validate) ShowValidation();
    }

    /// <returns>true if all fields valid.</returns>
    private bool ShowValidation()
    {
        var title = FeedbackRules.CheckTitle(TitleInput.Text);
        var description = FeedbackRules.CheckDescription(DescriptionInput.Text);
        var contact = FeedbackRules.CheckContact(ContactInput.Text);
        ShowFieldError(TitleError, title, FeedbackRules.TitleMin, FeedbackRules.TitleMax);
        ShowFieldError(DescriptionError, description, FeedbackRules.DescriptionMin, FeedbackRules.DescriptionMax);
        ShowFieldError(ContactError, contact, 0, FeedbackRules.ContactMax);
        return title is null && description is null && contact is null;
    }

    private void ShowFieldError(TextBlock target, FeedbackFieldError? error, int min, int max)
    {
        bool show = _validate && error is not null;
        target.Text = !show ? "" : error == FeedbackFieldError.TooShort ? Loc.F("Feedback.TooShort", min) : Loc.F("Feedback.TooLong", max);
        target.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnSend(object sender, RoutedEventArgs e)
    {
        if (_sending is not null || _result is FeedbackResult.Sent) return;
        _validate = true;
        if (!ShowValidation())
        {
            (TitleError.IsVisible ? TitleInput : DescriptionError.IsVisible ? DescriptionInput : (Control)ContactInput).Focus();
            return;
        }
        var draft = new FeedbackDraft(Kind, TitleInput.Text, DescriptionInput.Text, ContactInput.Text,
            Kind == FeedbackKind.Bug && AttachDetailsInput.IsChecked == true ? Diagnostics : null);
        _sending = new CancellationTokenSource();
        _result = null;
        SetSending(true);
        FeedbackResult result;
        try { result = await _send(draft, _sending.Token); }
        catch (OperationCanceledException) { return; } // window closed
        catch (Exception ex)
        {
            Log.Error("Feedback send failed", ex);
            result = new FeedbackResult.Offline();
        }
        finally
        {
            _sending?.Dispose();
            _sending = null;
        }
        if (!IsLoaded) return;
        _result = result;
        SetSending(false);
        ShowResult();
    }

    private void SetSending(bool sending)
    {
        FormPanel.IsEnabled = !sending;
        SendButton.IsEnabled = !sending;
        GitHubLink.IsEnabled = !sending;
        SendingPanel.Visibility = sending ? Visibility.Visible : Visibility.Collapsed;
        if (sending) { ErrorPanel.Visibility = Visibility.Collapsed; StartProgress(); }
        else ProgressBar.BeginAnimation(Canvas.LeftProperty, null);
    }

    private void StartProgress()
    {
        double width = Math.Max(ProgressTrack.ActualWidth, 200);
        ProgressBar.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(-ProgressBar.Width, width, TimeSpan.FromSeconds(1.2))
        {
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }

    /// <summary>Send result: success replaces form, error — line above buttons, text in fields stays.</summary>
    private void ShowResult()
    {
        bool sent = _result is FeedbackResult.Sent;
        FormPanel.Visibility = sent ? Visibility.Collapsed : Visibility.Visible;
        IntroText.Visibility = sent ? Visibility.Collapsed : Visibility.Visible;
        SuccessPanel.Visibility = sent ? Visibility.Visible : Visibility.Collapsed;
        SendButton.Visibility = CancelButton.Visibility = GitHubLink.Visibility = sent ? Visibility.Collapsed : Visibility.Visible;
        CloseButton.Visibility = sent ? Visibility.Visible : Visibility.Collapsed;
        CloseButton.IsCancel = CloseButton.IsDefault = sent;
        CancelButton.IsCancel = !sent;
        SendButton.IsDefault = !sent;
        if (_result is FeedbackResult.Sent { Id: var id })
            SuccessText.Text = id is null ? Loc.T("Feedback.SentNoId") : Loc.F("Feedback.Sent", id);
        var error = ErrorMessage(_result);
        ErrorText.Text = error ?? "";
        ErrorPanel.Visibility = error is null || _sending is not null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Send error text in current language; null — no error.</summary>
    public static string? ErrorMessage(FeedbackResult? result) => result switch
    {
        FeedbackResult.Offline => Loc.T("Feedback.ErrorOffline"),
        FeedbackResult.RateLimited r => FeedbackClient.RetryMinutes(r.RetryAfter) is { } minutes
            ? Loc.F("Feedback.ErrorRateLimitedMinutes", minutes)
            : Loc.T("Feedback.ErrorRateLimited"),
        FeedbackResult.Invalid { Message: { } message } => Loc.F("Feedback.ErrorInvalid", message),
        FeedbackResult.Invalid => Loc.T("Feedback.ErrorInvalidNoMessage"),
        FeedbackResult.ServerError s => Loc.F("Feedback.ErrorServer", s.Status),
        FeedbackResult.Failed f => Loc.F("Feedback.ErrorServer", f.Status),
        FeedbackResult.NotConfigured => Loc.T("Feedback.DevNotConfigured"),
        _ => null,
    };

    private void OnToggleDetails(object sender, RoutedEventArgs e)
    {
        _detailsShown = !_detailsShown;
        DetailsPreview.Visibility = _detailsShown ? Visibility.Visible : Visibility.Collapsed;
        Render();
    }

    private void OnAttachChanged(object sender, RoutedEventArgs e)
    {
        if (DetailsToggle is null) return; // at InitializeComponent
        DetailsToggle.IsEnabled = AttachDetailsInput.IsChecked == true;
        if (AttachDetailsInput.IsChecked != true && _detailsShown) OnToggleDetails(sender, e);
    }

    /// <summary>What goes into <c>diagnostics</c> line by line: UI language labels, values as-is.</summary>
    public static string PreviewText(FeedbackDiagnostics d)
    {
        var lines = new List<string>
        {
            $"{Loc.T("Feedback.DiagAppVersion")}: {d.AppVersion}",
            $"{Loc.T("Feedback.DiagOs")}: {d.OsVersion}",
            $"{Loc.T("Feedback.DiagLanguage")}: {d.UiLanguage}",
            $"{Loc.T("Feedback.DiagDpi")}: {d.Dpi}",
            $"{Loc.T("Feedback.DiagMonitors")}: {d.Monitors}",
            "",
            Loc.F("Feedback.DiagLog", d.LogTail.Length == 0 ? 0 : d.LogTail.Split('\n').Length),
        };
        if (d.LogTail.Length > 0) lines.Add(d.LogTail);
        return string.Join("\n", lines);
    }

    private void OnOpenGitHub(object sender, RoutedEventArgs e)
    {
        var url = GitHubIssueLink.Build(Kind, TitleInput.Text);
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { Log.Error("Open GitHub issue link failed", ex); }
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosing(CancelEventArgs e)
    {
        _sending?.Cancel();
        base.OnClosing(e);
    }

    /// <summary>
    /// Test instance: <c>--feedback bug|idea --scenario &lt;name&gt;</c> — filled fields and fake send without network
    /// (<c>sending</c> never finishes). <c>validation</c> — empty fields after “Send”, <c>details</c> — open
    /// details list, <c>not-configured</c> — “Send” via real App send (without <c>--feedback-endpoint</c>
    /// this state is “address not set”).
    /// </summary>
    public void ApplyDevScenario(string? name)
    {
        if (name is null) return;
        TitleInput.Text = Kind == FeedbackKind.Bug ? "Pin window loses its zoom after Esc" : "Blur tool with adjustable strength";
        DescriptionInput.Text = Kind == FeedbackKind.Bug
            ? "I pin a screenshot, zoom it to 200%, press Esc in the pin and open it again from history: the zoom is back at 100%."
            : "Sometimes the blur is too weak to hide text. A slider for the strength would help.";
        ContactInput.Text = "@screeny_tester";
        FeedbackResult? fake = name switch
        {
            "sent" => new FeedbackResult.Sent("K7Q2M"),
            "offline" => new FeedbackResult.Offline(),
            "rate-limited" => new FeedbackResult.RateLimited(TimeSpan.FromMinutes(14)),
            "invalid" => new FeedbackResult.Invalid("Description looks like spam."),
            "server" => new FeedbackResult.ServerError(503),
            _ => null,
        };
        switch (name)
        {
            case "validation":
                TitleInput.Text = "Hi";
                DescriptionInput.Text = "";
                OnSend(this, new RoutedEventArgs());
                return;
            case "details":
                Loaded += (_, _) => OnToggleDetails(this, new RoutedEventArgs());
                return;
            case "not-configured":
                break; // real App send: without --feedback-endpoint it does not go to network and doesn't create installId
            case "sending":
                _send = (_, ct) => Task.Delay(Timeout.Infinite, ct).ContinueWith<FeedbackResult>(_ => new FeedbackResult.Offline(), TaskScheduler.Default);
                break;
            default:
                if (fake is null) return;
                _send = (_, _) => Task.FromResult(fake);
                break;
        }
        Loaded += (_, _) => OnSend(this, new RoutedEventArgs());
    }
}

/// <summary>Technical details of this copy: version, Windows, language, window DPI, monitors, scrubbed log tail.</summary>
public static class FeedbackDiagnosticsCollector
{
    private const int SM_CMONITORS = 80;

    public static FeedbackDiagnostics Collect(string appVersion, Visual? visual)
    {
        int dpi = 96;
        if (visual is not null) dpi = (int)Math.Round(VisualTreeHelper.GetDpi(visual).PixelsPerInchX);
        int monitors = Math.Max(1, User32.GetSystemMetrics(SM_CMONITORS));
        return new FeedbackDiagnostics(appVersion, OsVersion(Environment.OSVersion.Version, RuntimeInformation.OSArchitecture.ToString()),
            Loc.Culture.Name, dpi, monitors, LogScrubber.ReadTail(Log.FilePath, ScrubContext.Current()));
    }

    /// <summary>“Windows 11 (10.0.26200, X64)”: build 22000 and newer — Windows 11.</summary>
    public static string OsVersion(Version v, string architecture)
        => $"Windows {(v.Major == 10 && v.Build >= 22000 ? "11" : v.Major == 10 ? "10" : $"{v.Major}.{v.Minor}")} ({v.Major}.{v.Minor}.{v.Build}, {architecture})";
}

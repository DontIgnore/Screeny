using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Screeny.Localization;
using Screeny.Logging;
using Screeny.Themes;

namespace Screeny.Updates;

/// <summary>
/// “Version X available”: changelog and choice — update now, later or skip version.
/// Nothing installed without pressing “Update and restart”. On language change title and buttons update;
/// changelog stays in language chosen at window open (<see cref="ReleaseNotes.ForLanguage"/>).
/// </summary>
public partial class UpdateWindow : ChromeWindow
{
    /// <summary>Download with progress (0–100) and apply; after successful call app exits.</summary>
    public delegate Task InstallHandler(Action<int> progress, CancellationToken ct);

    private readonly InstallHandler _install;
    private readonly Func<bool> _skip;
    private CancellationTokenSource? _download;

    public UpdateCandidate Update { get; }

    /// <param name="prompt">What to show from changelog (<see cref="UpdatePrompt.Decide"/>).</param>
    public UpdateWindow(UpdateCandidate update, UpdatePrompt prompt, string? currentVersion, InstallHandler install, Func<bool> skip)
    {
        InitializeComponent();
        Update = update;
        _install = install;
        _skip = skip;
        _currentVersion = currentVersion;
        Loc.Bind(LaterButton, ContentProperty, "Update.Later");
        ShowHeading();
        Loc.Changed += ShowHeading;
        Closed += (_, _) => Loc.Changed -= ShowHeading;
        CurrentText.Visibility = currentVersion is null ? Visibility.Collapsed : Visibility.Visible;
        RenderNotes(prompt.Notes, prompt.OnlyDisabledFeatures);
        Loaded += (_, _) => UpdateButton.Focus();
    }

    private readonly string? _currentVersion;

    private void ShowHeading()
    {
        HeadingText.Text = Loc.F("Update.Available", Update.Version);
        CurrentText.Text = _currentVersion is null ? "" : Loc.F("Update.Current", _currentVersion);
    }

    private void RenderNotes(IReadOnlyList<NoteBlock> blocks, bool onlyDisabledFeatures)
    {
        if (blocks.Count == 0)
        {
            var empty = Loc.T(onlyDisabledFeatures ? "Update.OnlyDisabled" : "Update.NoNotes");
            NotesPanel.Children.Add(Text([new NoteInline(empty)], "Body", Brush("Brush.Ink50")));
            return;
        }
        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            bool first = i == 0;
            FrameworkElement element = block.Kind switch
            {
                NoteBlockKind.Heading1 or NoteBlockKind.Heading2 => Text(block.Inlines, "H1", null, size: block.Kind == NoteBlockKind.Heading1 ? 16 : 14),
                NoteBlockKind.Heading3 => Text(block.Inlines, "Body", null, weight: FontWeights.SemiBold),
                NoteBlockKind.Bullet or NoteBlockKind.Numbered => ListItem(block),
                _ => Text(block.Inlines, "Body", Brush("Brush.Ink70")),
            };
            bool heading = block.Kind is NoteBlockKind.Heading1 or NoteBlockKind.Heading2 or NoteBlockKind.Heading3;
            bool listAfterList = i > 0 && IsListItem(blocks[i - 1]) && IsListItem(block);
            element.Margin = new Thickness(IsListItem(block) ? block.Level * 20 : 0, first ? 0 : heading ? 14 : listAfterList ? 4 : 8, 0, 0);
            NotesPanel.Children.Add(element);
        }
    }

    private static bool IsListItem(NoteBlock b) => b.Kind is NoteBlockKind.Bullet or NoteBlockKind.Numbered;

    private FrameworkElement ListItem(NoteBlock block)
    {
        var grid = new Grid(); // nesting indent set by RenderNotes with vertical
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var marker = Text([new NoteInline(block.Kind == NoteBlockKind.Numbered ? $"{block.Number}." : "•")], "Body", Brush("Brush.Ink50"));
        var body = Text(block.Inlines, "Body", Brush("Brush.Ink70"));
        Grid.SetColumn(body, 1);
        grid.Children.Add(marker);
        grid.Children.Add(body);
        return grid;
    }

    private TextBlock Text(IReadOnlyList<NoteInline> inlines, string style, Brush? foreground, double? size = null, FontWeight? weight = null)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
        text.SetResourceReference(StyleProperty, style);
        if (foreground is not null) text.Foreground = foreground;
        if (size is { } s) text.FontSize = s;
        if (weight is { } w) text.FontWeight = w;
        foreach (var inline in inlines)
        {
            var run = new Run(inline.Text);
            if (inline.Bold) { run.FontWeight = FontWeights.SemiBold; run.Foreground = Brush("Brush.Ink"); }
            if (inline.Code) { run.SetResourceReference(TextElement.FontFamilyProperty, "Font.Mono"); run.FontSize = 12; }
            text.Inlines.Add(run);
        }
        return text;
    }

    private Brush? Brush(string key) => TryFindResource(key) as Brush;

    private async void OnUpdate(object sender, RoutedEventArgs e)
    {
        if (_download is not null) return;
        _download = new CancellationTokenSource();
        UpdateButton.IsEnabled = false;
        SkipButton.IsEnabled = false;
        Loc.Bind(LaterButton, ContentProperty, "Common.Cancel");
        ErrorText.Visibility = Visibility.Collapsed;
        ShowStatus(Loc.T("Update.Downloading"));
        try
        {
            await _install(p => Dispatcher.BeginInvoke(() => ShowStatus(Loc.F("Update.DownloadingPercent", Math.Clamp(p, 0, 100)))), _download.Token);
            ShowStatus(Loc.T("Update.Restarting"));
        }
        catch (OperationCanceledException) when (_download?.IsCancellationRequested == true)
        {
            ResetButtons();
            StatusText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            Log.Error("Update install failed", ex);
            ResetButtons();
            StatusText.Visibility = Visibility.Collapsed;
            ErrorText.Text = Loc.F("Update.Failed", ex.Message);
            ErrorText.Visibility = Visibility.Visible;
        }
    }

    private void ResetButtons()
    {
        _download?.Dispose();
        _download = null;
        UpdateButton.IsEnabled = true;
        SkipButton.IsEnabled = true;
        Loc.Bind(LaterButton, ContentProperty, "Update.Later");
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Visibility = Visibility.Visible;
    }

    private void OnLater(object sender, RoutedEventArgs e)
    {
        if (_download is not null) { _download.Cancel(); return; }
        Close();
    }

    private void OnSkip(object sender, RoutedEventArgs e)
    {
        if (!_skip())
        {
            ErrorText.Text = Loc.T("Update.SkipFailed");
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _download?.Cancel();
        base.OnClosing(e);
    }
}

using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Screeny.Localization;
using Screeny.Modules;

namespace Screeny.Settings;

/// <summary>
/// Блок «Поиск по смыслу» в настройках, под «Историей снимков». Модуль не установлен — согласие с размером загрузки и
/// «Скачать» (с прогрессом и отменой); установлен — сколько снимков проиндексировано и «Удалить модуль»; стоит старая
/// версия — «Обновить модуль». Отдельной галочки нет: модуль установлен — поиск включён. Скачивание и удаление
/// выполняются сразу, не по «Сохранить».
/// </summary>
public sealed class SemanticPanel
{
    private readonly IModuleHost _host;
    private readonly Func<(int Done, int Total)> _progress;
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 17, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _hint = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 17, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Grid _bar = new() { Height = 4, Margin = new Thickness(0, 12, 0, 0) };
    private readonly ColumnDefinition _done = new(), _rest = new();
    private readonly Button _download = new() { Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _cancel = new() { Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _remove = new() { Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _undo = new() { Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _caption = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 17, Margin = new Thickness(0, 4, 0, 0) };
    private bool _ocrOn = true;

    public StackPanel Root { get; } = new() { Margin = new Thickness(28, 12, 0, 0) };

    /// <param name="progress">Сколько снимков проиндексировано из скольких.</param>
    public SemanticPanel(IModuleHost host, Func<(int Done, int Total)> progress)
    {
        _host = host;
        _progress = progress;
        var title = new TextBlock();
        Loc.Bind(title, TextBlock.TextProperty, "Semantic.Title");
        title.SetResourceReference(FrameworkElement.StyleProperty, "Body");
        Loc.Bind(_cancel, ContentControl.ContentProperty, "Common.Cancel");
        Loc.Bind(_remove, ContentControl.ContentProperty, "PreciseOcr.Remove");
        Loc.Bind(_undo, ContentControl.ContentProperty, "PreciseOcr.Undo");
        Loc.Bind(_hint, TextBlock.TextProperty, "Semantic.NeedsOcr");
        foreach (var t in new[] { _caption, _state, _hint }) t.SetResourceReference(FrameworkElement.StyleProperty, "Caption");
        _error.SetResourceReference(FrameworkElement.StyleProperty, "ErrorText");

        _bar.ColumnDefinitions.Add(_done);
        _bar.ColumnDefinitions.Add(_rest);
        var track = new Border { CornerRadius = new CornerRadius(2) };
        track.SetResourceReference(Border.BackgroundProperty, "Brush.Stroke");
        Grid.SetColumnSpan(track, 2);
        var fill = new Border { CornerRadius = new CornerRadius(2) };
        fill.SetResourceReference(Border.BackgroundProperty, "Brush.Accent");
        _bar.Children.Add(track);
        _bar.Children.Add(fill);
        Loc.Bind(_bar, AutomationProperties.NameProperty, "PreciseOcr.Progress");

        _download.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        _download.Click += (_, _) => _host.StartDownload();
        _cancel.Click += (_, _) => _host.CancelDownload();
        _remove.Click += (_, _) => _host.Remove();
        _undo.Click += (_, _) => _host.UndoRemove();
        _actions.Children.Add(_download);
        _actions.Children.Add(_undo);
        _actions.Children.Add(_cancel);
        _actions.Children.Add(_remove);

        foreach (var e in new UIElement[] { title, _caption, _bar, _state, _hint, _actions, _error }) Root.Children.Add(e);
        _host.Changed += Show;
        Root.Unloaded += (_, _) => _host.Changed -= Show;
        Show();
    }

    /// <summary>Функцию «Распознавание текста и QR» включили или выключили в окне (без неё поиск по смыслу не работает).</summary>
    public void SetOcrOn(bool on)
    {
        _ocrOn = on;
        Show();
    }

    /// <summary>Язык сменился или поменялся счёт проиндексированных: подписи собираются заново.</summary>
    public void Refresh() => Show();

    private void Show()
    {
        var s = _host.Status;
        var size = OcrModule.MegabytesText(s.DownloadBytes > 0 ? s.DownloadBytes : SemanticModule.ZipSize);
        _caption.Text = Loc.F("Semantic.Caption", size);
        void Visible(UIElement e, bool on) => e.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Visible(_bar, s.Downloading);
        Visible(_cancel, s.Downloading);
        Visible(_download, !s.Downloading && !s.Ready && !s.RemovalPending);
        Visible(_remove, !s.Downloading && (s.Ready || s.Outdated || s.Installed is not null));
        Visible(_undo, !s.Downloading && s.RemovalPending);
        Visible(_hint, !_ocrOn);
        _download.Content = Loc.F(s.Outdated ? "PreciseOcr.Update" : "PreciseOcr.Download", size);
        _download.IsEnabled = s.SourceAvailable;
        _done.Width = new GridLength(Math.Clamp(s.Progress, 0, 100), GridUnitType.Star);
        _rest.Width = new GridLength(100 - Math.Clamp(s.Progress, 0, 100), GridUnitType.Star);

        var (done, total) = _progress();
        string? state = s switch
        {
            { Downloading: true } => Loc.F("PreciseOcr.Downloading", s.Progress),
            { RemovalPending: true } => Loc.T("PreciseOcr.RemovalPending"),
            { Outdated: true } => Loc.F("Semantic.Outdated", s.Installed, s.Expected),
            { Ready: true } when total > 0 && done < total => Loc.T("Semantic.Ready") + " " + Loc.F("Semantic.Indexing", done, total),
            { Ready: true } when total > 0 => Loc.T("Semantic.Ready") + " " + Loc.F("Semantic.Indexed", total),
            { Ready: true } => Loc.T("Semantic.Ready"),
            _ when !s.SourceAvailable => "Dev instance downloads the module only from a local folder (--module-source).",
            _ => null,
        };
        _state.Text = state ?? "";
        Visible(_state, state is not null);
        _error.Text = s.Error ?? "";
        Visible(_error, s.Error is not null);
    }
}

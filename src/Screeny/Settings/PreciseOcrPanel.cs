using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Screeny.Localization;
using Screeny.Modules;

namespace Screeny.Settings;

/// <summary>
/// Блок «Точное распознавание (PaddleOCR)» в настройках, под «Распознаванием текста и QR». Модуль не установлен —
/// «Скачать» (с прогрессом и отменой); установлен — галочка «Использовать» (сохраняется вместе с окном) и «Удалить модуль»;
/// стоит старая версия — «Обновить модуль». Скачивание и удаление выполняются сразу, не по «Сохранить».
/// </summary>
public sealed class PreciseOcrPanel
{
    private readonly IModuleHost _host;
    private readonly StackPanel _actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
    private readonly TextBlock _state = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 17, Margin = new Thickness(0, 8, 0, 0) };
    private readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _use = new() { Margin = new Thickness(0, 10, 0, 0) };
    private readonly Grid _bar = new() { Height = 4, Margin = new Thickness(0, 12, 0, 0) };
    private readonly ColumnDefinition _done = new(), _rest = new();
    // Отступ справа у каждой кнопки: скрытые не сдвигают видимые.
    private readonly Button _download = new() { Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _cancel = new() { Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _remove = new() { Margin = new Thickness(0, 0, 8, 0) };
    private readonly Button _undo = new() { Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBlock _caption = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 17, Margin = new Thickness(0, 4, 0, 0) };

    public StackPanel Root { get; } = new() { Margin = new Thickness(28, 12, 0, 0) };

    /// <param name="use">Текущее значение <see cref="AppSettings.PreciseOcr"/>.</param>
    public PreciseOcrPanel(IModuleHost host, bool use)
    {
        _host = host;
        var title = new TextBlock();
        Loc.Bind(title, TextBlock.TextProperty, "PreciseOcr.Title");
        title.SetResourceReference(FrameworkElement.StyleProperty, "Body");
        Loc.Bind(_use, ContentControl.ContentProperty, "PreciseOcr.Use");
        Loc.Bind(_cancel, ContentControl.ContentProperty, "Common.Cancel");
        Loc.Bind(_remove, ContentControl.ContentProperty, "PreciseOcr.Remove");
        Loc.Bind(_undo, ContentControl.ContentProperty, "PreciseOcr.Undo");
        foreach (var t in new[] { _caption, _state }) t.SetResourceReference(FrameworkElement.StyleProperty, "Caption");
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

        _use.IsChecked = use;
        _download.SetResourceReference(FrameworkElement.StyleProperty, "AccentButton");
        _download.Click += (_, _) => _host.StartDownload();
        _cancel.Click += (_, _) => _host.CancelDownload();
        _remove.Click += (_, _) => _host.Remove();
        _undo.Click += (_, _) => _host.UndoRemove();
        _actions.Children.Add(_download);
        _actions.Children.Add(_undo);
        _actions.Children.Add(_cancel);
        _actions.Children.Add(_remove);

        foreach (var e in new UIElement[] { title, _caption, _use, _bar, _state, _actions, _error }) Root.Children.Add(e);
        _host.Changed += Show;
        Root.Unloaded += (_, _) => _host.Changed -= Show;
        Show();
    }

    /// <summary>Значение «Использовать» для сохранения; null — модуля нужной версии нет, галочки не видно (остаётся как было).</summary>
    public bool? Use => _host.Status.Ready ? _use.IsChecked == true : null;

    /// <summary>Загрузка закончилась и согласие уже записано в настройки (см. App): галочку — по ним.</summary>
    public void SetUse(bool use) => _use.IsChecked = use;

    /// <summary>Язык интерфейса сменился: подписи с подстановками собираются заново.</summary>
    public void Refresh() => Show();

    private void Show()
    {
        var s = _host.Status;
        // Сколько качать сейчас: модуль и общий ORT, если его ещё нет (поставил смысловой поиск — только модуль).
        var size = OcrModule.MegabytesText(s.DownloadBytes > 0 ? s.DownloadBytes : OcrModule.ZipSize);
        _caption.Text = Loc.F("PreciseOcr.Caption", size);
        void Visible(UIElement e, bool on) => e.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        Visible(_use, s.Ready && !s.Downloading);
        Visible(_bar, s.Downloading);
        Visible(_cancel, s.Downloading);
        Visible(_download, !s.Downloading && !s.Ready && !s.RemovalPending);
        Visible(_remove, !s.Downloading && (s.Ready || s.Outdated));
        Visible(_undo, !s.Downloading && s.RemovalPending);
        _download.Content = Loc.F(s.Outdated ? "PreciseOcr.Update" : "PreciseOcr.Download", size);
        _download.IsEnabled = s.SourceAvailable;
        _done.Width = new GridLength(Math.Clamp(s.Progress, 0, 100), GridUnitType.Star);
        _rest.Width = new GridLength(100 - Math.Clamp(s.Progress, 0, 100), GridUnitType.Star);

        string? state = s switch
        {
            { Downloading: true } => Loc.F("PreciseOcr.Downloading", s.Progress),
            { RemovalPending: true } => Loc.T("PreciseOcr.RemovalPending"),
            { Outdated: true } => Loc.F("PreciseOcr.Outdated", s.Installed, OcrModule.Version),
            { Ready: true } => Loc.T("PreciseOcr.Ready"),
            _ when !s.SourceAvailable => "Dev instance downloads the module only from a local folder (--module-source).",
            _ => null,
        };
        _state.Text = state ?? "";
        Visible(_state, state is not null);
        _error.Text = s.Error ?? "";
        Visible(_error, s.Error is not null);
    }
}

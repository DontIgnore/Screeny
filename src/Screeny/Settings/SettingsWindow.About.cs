using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using Screeny.Localization;
using Screeny.Logging;
using Screeny.Updates;

namespace Screeny.Settings;

/// <summary>
/// Раздел «О программе»: версия, ручная проверка обновлений и ссылки. Проверку и окно обновления ведёт App (тот же путь,
/// что у пункта трея «Проверить обновления…»); здесь только итог строкой под кнопкой.
/// </summary>
public partial class SettingsWindow
{
    private AboutUpdateStatus _aboutStatus = AboutUpdateStatus.Idle;
    private string _version = AppVersion.Choose(null, AppVersion.Informational());
    private bool _closed;

    private static App? CurrentApp => Application.Current as App;

    private void InitAbout()
    {
        Closed += (_, _) => _closed = true;
        if (CurrentApp is { } app) _version = app.CurrentVersion;
        VersionText.Text = $"Screeny {_version}";
        _aboutStatus = AboutUpdateStatus.Initial(CurrentApp?.CanUpdateItself == true);
        ShowAboutStatus();
    }

    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        if (CurrentApp is not { } app) return;
        var previous = _aboutStatus;
        _aboutStatus = AboutUpdateStatus.Checking;
        ShowAboutStatus();
        CheckUpdatesButton.IsEnabled = false;
        UpdateCheckResult? result;
        try { result = await app.CheckForUpdatesAsync(); }
        finally { if (!_closed) CheckUpdatesButton.IsEnabled = true; }
        if (_closed) return;
        _aboutStatus = AboutUpdateStatus.From(result, previous);
        ShowAboutStatus();
    }

    private void OnInstallUpdate(object sender, RoutedEventArgs e)
    {
        if (_aboutStatus.Update is { } update) CurrentApp?.ShowUpdateWindow(update);
    }

    /// <summary>Строка состояния из сохранённого состояния: при смене языка собирается заново.</summary>
    private void ShowAboutStatus()
    {
        var s = _aboutStatus;
        string? text = s.State switch
        {
            AboutUpdateState.Checking => Loc.T("About.Checking"),
            AboutUpdateState.UpToDate => Loc.T("Updates.UpToDateTitle"),
            AboutUpdateState.Available => Loc.F("Update.Available", s.Update?.Version),
            AboutUpdateState.Failed => Loc.F("About.CheckFailed", s.ReasonKey is { } key ? Loc.T(key) : s.ReasonText),
            AboutUpdateState.NotUpdatable => Loc.T("About.NotUpdatable"),
            _ => null,
        };
        UpdateStatus.Text = text ?? "";
        UpdateStatus.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        if (s.State == AboutUpdateState.Failed) UpdateStatus.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "Brush.DangerText");
        else UpdateStatus.ClearValue(System.Windows.Controls.TextBlock.ForegroundProperty);
        InstallUpdateButton.Visibility = s.State == AboutUpdateState.Available ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnOpenReleasePage(object sender, RoutedEventArgs e) => OpenLink(AppVersion.ReleasePageUrl(_version));

    private void OnOpenLicense(object sender, RoutedEventArgs e) => OpenLink(AppVersion.LicenseUrl);

    private void OnOpenRepository(object sender, RoutedEventArgs e) => OpenLink(VelopackUpdateClient.RepositoryUrl);

    private static void OpenLink(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { Log.Error($"Open link failed: {url}", ex); }
    }
}

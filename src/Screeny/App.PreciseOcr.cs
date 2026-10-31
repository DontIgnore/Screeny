using System.Net.Http;
using Screeny.Localization;
using Screeny.Logging;
using Screeny.Modules;
using Screeny.Ocr.Paddle;

namespace Screeny;

/// <summary>
/// Precise text recognition (downloadable PaddleOCR module). Separate file so App.xaml.cs changes minimally:
/// from there <see cref="InitPreciseOcr"/> (at startup) and <see cref="DisposePreciseOcr"/> (on exit) are called.
/// </summary>
public partial class App
{
    private PreciseOcrController? _ocrModule;
    private PreciseOcrService? _preciseOcr;
    private bool _moduleUpdateHinted;
    // No overall timeout: on slow network 30 MB downloads longer than any reasonable limit. Hangs are caught by idle
    // timeout in ModuleInstaller (response and each data chunk within 30 s).
    private static readonly Lazy<HttpClient> ModuleHttp = new(() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    /// <summary>Module for settings window and wizard.</summary>
    internal IPreciseOcrHost? OcrModuleHost => _ocrModule;

    /// <summary>Refinement for text mode (overlay).</summary>
    internal IOcrRefiner? OcrRefiner => _preciseOcr;

    /// <summary>
    /// <c>isOn</c> for changelog: <see cref="PreciseOcrRules.NotesTag"/> label — for “precise recognition active”,
    /// others — for disabled features.
    /// </summary>
    private Func<string, bool> NotesFilter => PreciseOcrRules.NotesFilter(Settings, () => _ocrModule?.InstalledVersion);

    private void InitPreciseOcr()
    {
        var installer = new ModuleInstaller(OcrModule.ModulesRoot(IsDev), OcrModule.Spec);
        var kind = ModuleInstaller.ChooseSource(IsDev, Args.ModuleSource);
        _preciseOcr = new PreciseOcrService(
            () => PreciseOcrRules.IsActive(Settings, _ocrModule?.InstalledVersion) ? _ocrModule?.ReadyFolder : null,
            OnTextModeUsed);
        _ocrModule = new PreciseOcrController(installer,
            () => kind switch
            {
                ModuleSourceKind.Folder => new FolderModuleSource(Args.ModuleSource!, OcrModule.ZipName(OcrModule.Version)),
                ModuleSourceKind.Github => new HttpModuleSource(ModuleHttp.Value, OcrModule.DownloadUrl(OcrModule.Version)),
                _ => null, // dev without --module-source: no network
            },
            dir => OrtNative.LoadedFrom is { } loaded && string.Equals(Path.GetFullPath(loaded), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase))
        {
            LoadError = () => _preciseOcr?.LoadError,
        };
        _ocrModule.Finished += OnOcrModuleFinished;
        _ocrModule.Removed += OnOcrModuleRemoved;
        _ocrModule.Restored += OnOcrModuleRestored;
        _ = _ocrModule.CleanupAsync();
    }

    /// <summary>
    /// Download finished. Success is also consent: “Use” is enabled and saved immediately (that's why button was pressed).
    /// From wizard result comes as notification: its window is already closed by then.
    /// </summary>
    private void OnOcrModuleFinished(bool ok, string? error, bool notifyInTray)
    {
        if (IsShuttingDown) return;
        if (ok)
        {
            _preciseOcr?.Reset();
            SetPreciseOcrConsent(true);
        }
        if (!notifyInTray) return;
        Notify(ok ? Loc.T("PreciseOcr.Installed") : Loc.F("PreciseOcr.DownloadFailed", error));
    }

    /// <summary>Wizard: “Download precise recognition” checked and choice applied.</summary>
    private void DownloadPreciseOcrFromWizard() => _ocrModule?.StartDownload(notifyInTray: true);

    /// <summary>
    /// Ctrl+T but module outdated (Screeny updated, module needs new version): once per run — tray hint.
    /// Module itself is not re-downloaded without pressing.
    /// </summary>
    private void OnTextModeUsed()
    {
        if (_moduleUpdateHinted || !PreciseOcrRules.NeedsModuleUpdate(Settings, _ocrModule?.InstalledVersion)) return;
        _moduleUpdateHinted = true;
        Log.Error($"OCR module {_ocrModule?.InstalledVersion} is outdated (needs {OcrModule.Version}); using Windows OCR");
        Notify(Loc.T("PreciseOcr.NeedsUpdate"));
    }

    /// <summary>Deleted module in settings: consent removed (download again — explicit action again).</summary>
    private void OnOcrModuleRemoved()
    {
        _preciseOcr?.Reset();
        SetPreciseOcrConsent(false);
    }

    /// <summary>“Don't delete” after “Delete module”: deletion removed consent — we restore it together with module.</summary>
    private void OnOcrModuleRestored()
    {
        _preciseOcr?.Reset();
        SetPreciseOcrConsent(true);
    }

    /// <summary>Consent in settings and “Use” checkbox in open settings window are same value.</summary>
    private void SetPreciseOcrConsent(bool on)
    {
        if (Settings.PreciseOcr != on)
        {
            Settings.PreciseOcr = on;
            SaveSettings();
        }
        _settingsWindow?.RefreshPreciseOcr(on);
    }

    private void DisposePreciseOcr()
    {
        _ocrModule?.CancelDownload();
        _preciseOcr?.Dispose();
    }
}

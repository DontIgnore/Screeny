using System.Net.Http;
using Screeny.Localization;
using Screeny.Logging;
using Screeny.Modules;
using Screeny.Ocr.Paddle;

namespace Screeny;

/// <summary>
/// Точное распознавание текста (скачиваемый модуль PaddleOCR). Отдельный файл, чтобы App.xaml.cs менялся минимально:
/// оттуда вызываются <see cref="InitPreciseOcr"/> (при старте) и <see cref="DisposePreciseOcr"/> (при выходе).
/// </summary>
public partial class App
{
    private ModuleController? _ocrModule;
    private PreciseOcrService? _preciseOcr;
    private bool _moduleUpdateHinted;
    // Без общего таймаута: на медленной сети 30 МБ качаются дольше любого разумного предела. Зависание ловит таймаут
    // бездействия в ModuleInstaller (ответ и каждый кусок данных — за 30 с).
    private static readonly Lazy<HttpClient> ModuleHttp = new(() => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

    /// <summary>Модуль для окна настроек и мастера.</summary>
    internal IModuleHost? OcrModuleHost => _ocrModule;

    /// <summary>Уточнение для режима текста (оверлей).</summary>
    internal IOcrRefiner? OcrRefiner => _preciseOcr;

    /// <summary>
    /// <c>isOn</c> для списка изменений: метка <see cref="PreciseOcrRules.NotesTag"/> — по «точное распознавание активно»,
    /// остальные — по выключенным функциям.
    /// </summary>
    private Func<string, bool> NotesFilter => PreciseOcrRules.NotesFilter(Settings, () => _ocrModule?.InstalledVersion);

    private void InitPreciseOcr()
    {
        var installer = new ModuleInstaller(OcrModule.ModulesRoot(IsDev), OcrModule.Spec);
        _preciseOcr = new PreciseOcrService(
            () => PreciseOcrRules.IsActive(Settings, _ocrModule?.InstalledVersion) ? _ocrModule?.ReadyFolder : null,
            OnTextModeUsed);
        _ocrModule = new ModuleController(installer, ModuleSource, ModuleFolderInUse, [SemanticModule.Spec])
        {
            LoadError = () => _preciseOcr?.LoadError,
        };
        _ocrModule.Finished += OnOcrModuleFinished;
        _ocrModule.Removed += OnOcrModuleRemoved;
        _ocrModule.Restored += OnOcrModuleRestored;
        _ = _ocrModule.CleanupAsync();
    }

    /// <summary>Откуда брать архив модуля или общего компонента: <c>--module-source</c> (dev) — папка, обычный запуск — GitHub.</summary>
    private IModuleSource? ModuleSource(ModuleSpec spec) => ModuleInstaller.ChooseSource(IsDev, Args.ModuleSource) switch
    {
        ModuleSourceKind.Folder => new FolderModuleSource(Args.ModuleSource!, spec.ZipName),
        ModuleSourceKind.Github when spec.DownloadUrl is { } url => new HttpModuleSource(ModuleHttp.Value, url),
        _ => null, // dev без --module-source: никакой сети
    };

    /// <summary>Папку держит загруженный в процесс нативный ORT (общий компонент): удаляется после перезапуска.</summary>
    private static bool ModuleFolderInUse(string dir)
        => OrtNative.LoadedFrom is { } loaded && string.Equals(Path.GetFullPath(loaded), Path.GetFullPath(dir), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Загрузка закончилась. Успех — это и согласие: «Использовать» включается и сохраняется сразу (кнопку нажали ради этого).
    /// Из мастера итог приходит уведомлением: его окно к этому времени уже закрыто.
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

    /// <summary>Мастер: «Скачать точное распознавание» отмечено и выбор применён.</summary>
    private void DownloadPreciseOcrFromWizard() => _ocrModule?.StartDownload(notifyInTray: true);

    /// <summary>
    /// Ctrl+T, а модуль устаревший (Screeny обновился, модулю нужна новая версия): один раз за запуск — подсказка в трее.
    /// Сам модуль не перекачивается без нажатия.
    /// </summary>
    private void OnTextModeUsed()
    {
        if (_moduleUpdateHinted || !PreciseOcrRules.NeedsModuleUpdate(Settings, _ocrModule?.InstalledVersion)) return;
        _moduleUpdateHinted = true;
        Log.Error($"OCR module {_ocrModule?.InstalledVersion} is outdated (needs {OcrModule.Version}); using Windows OCR");
        Notify(Loc.T("PreciseOcr.NeedsUpdate"));
    }

    /// <summary>Удалили модуль в настройках: согласие снимается (скачать снова — снова явным действием).</summary>
    private void OnOcrModuleRemoved()
    {
        _preciseOcr?.Reset();
        SetPreciseOcrConsent(false);
    }

    /// <summary>«Не удалять» после «Удалить модуль»: удаление сняло согласие — возвращаем его вместе с модулем.</summary>
    private void OnOcrModuleRestored()
    {
        _preciseOcr?.Reset();
        SetPreciseOcrConsent(true);
    }

    /// <summary>Согласие в настройках и галочка «Использовать» в открытом окне настроек — одно и то же значение.</summary>
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

using Screeny.Features;
using Screeny.History;
using Screeny.Localization;
using Screeny.Logging;
using Screeny.Modules;
using Screeny.Semantic;

namespace Screeny;

/// <summary>
/// Смысловой поиск по истории (скачиваемый модуль EmbeddingGemma 2). Отдельный файл, чтобы App.xaml.cs менялся минимально:
/// оттуда вызываются <see cref="InitSemantic"/> (при старте), <see cref="StartSemanticIndex"/> и <see cref="DisposeSemantic"/>.
/// Модуль установлен — поиск включён; работает только вместе с функциями «История» и «Распознавание текста и QR».
/// </summary>
public partial class App
{
    private ModuleController? _semanticModule;
    private SemanticService? _semantic;
    private SemanticSearchProvider? _semanticSearch;
    private SemanticIndexer? _semanticIndexer;

    /// <summary>Модуль для окна настроек.</summary>
    internal IModuleHost? SemanticModuleHost => _semanticModule;

    /// <summary>Сколько снимков проиндексировано из скольких (для настроек).</summary>
    internal (int Done, int Total) SemanticProgress => _semanticIndexer?.Progress ?? (0, 0);

    /// <summary>Модуль нужной версии на месте и функции «История» и «Распознавание» включены.</summary>
    private bool SemanticOn => !IsShuttingDown && History is not null && Settings.IsFeatureOn(FeatureCatalog.Ocr) && _semanticModule?.ReadyFolder is not null;

    /// <summary>Поиск в окне истории есть: по тексту (есть языки OCR) или по смыслу.</summary>
    private bool HistorySearchOn => TextSearchOn || SemanticOn;

    private void InitSemantic()
    {
        var installer = new ModuleInstaller(OcrModule.ModulesRoot(IsDev), SemanticModule.Spec);
        _semanticModule = new ModuleController(installer, ModuleSource, ModuleFolderInUse, [OcrModule.Spec])
        {
            LoadError = () => _semantic?.LoadError,
            LoadErrorText = e => Loc.F("Semantic.LoadFailed", e),
        };
        _semantic = new SemanticService(() => SemanticOn ? _semanticModule.ReadyFolder : null);
        _semanticSearch = new SemanticSearchProvider(_semantic);
        _semanticIndexer = new SemanticIndexer(() => History, () => SemanticOn, () => _capturing, () => TextIndexOn,
            new ServiceItemEncoder(_semantic), _imageSaver.Queue, throttle: TimeSpan.FromSeconds(IsDev ? 0 : 1));
        _semanticIndexer.Indexed += (path, vectors) =>
        {
            _semanticSearch.Update(path, vectors);
            Dispatcher.BeginInvoke(() => _historyWindow?.OnSemanticIndexed());
        };
        _semanticIndexer.ProgressChanged += () => Dispatcher.BeginInvoke(() => _settingsWindow?.RefreshSemantic());
        // Новый снимок — после распознавания его текста (вектор текста входит в индекс).
        _textIndexer!.Indexed += (path, _) => { if (SemanticOn) _semanticIndexer.Add(path); };
        _semanticModule.Finished += OnSemanticModuleFinished;
        _semanticModule.Removed += OnSemanticModuleRemoved;
        _semanticModule.Restored += () => { _semantic.Reset(); StartSemanticIndex(TimeSpan.FromSeconds(1)); };
    }

    /// <summary>Догоняющий проход (при старте, после смены функций или папки, после установки модуля).</summary>
    private void StartSemanticIndex(TimeSpan delay)
    {
        _semanticIndexer?.Stop();
        _semanticSearch?.Clear();
        if (SemanticOn) _semanticIndexer?.Start(delay);
        if (_historyWindow is not null) _historyWindow.SearchEnabled = HistorySearchOn;
    }

    /// <summary>Снимок сохранён в историю: без распознавания текста индексируется сразу, иначе — после него.</summary>
    private void OnSemanticSnapshotSaved(string path)
    {
        if (SemanticOn && !TextIndexOn) _semanticIndexer?.Add(path);
    }

    private void OnSemanticModuleFinished(bool ok, string? error, bool notifyInTray)
    {
        if (IsShuttingDown || !ok) return;
        _semantic?.Reset();
        StartSemanticIndex(TimeSpan.FromSeconds(1));
        Notify(Loc.T("Semantic.Installed"));
    }

    /// <summary>
    /// Модуль удалён: индексация останавливается, а векторы снимков удаляются в фоне — без модели они бесполезны,
    /// а описывают содержимое снимков.
    /// </summary>
    private void OnSemanticModuleRemoved()
    {
        _semanticIndexer?.Stop();
        _semanticSearch?.Clear();
        _semantic?.Reset();
        if (History is { } store)
            _ = _imageSaver.Queue.Enqueue(() => { Log.Error($"Semantic vectors deleted after module removal: {store.DeleteVectors()}"); return 0; });
        if (_historyWindow is not null) _historyWindow.SearchEnabled = HistorySearchOn;
        _settingsWindow?.RefreshSemantic();
    }

    private void DisposeSemantic()
    {
        _semanticIndexer?.Stop();
        _semanticModule?.CancelDownload();
        _semantic?.Dispose();
    }
}

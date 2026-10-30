using Screeny.Overlay;
using Screeny.Overlay.Tools;
using Screeny.Settings;

namespace Screeny.Features;

/// <summary>
/// Единая проверка выключенных функций (<see cref="FeatureCatalog"/>). Её спрашивают и при сборке панели, и в каждом
/// обработчике клавиш и действий: скрытая кнопка с живой клавишей — главная ошибка, которой здесь не должно быть.
/// Настройки читаются каждый раз заново: окно настроек меняет тот же объект <see cref="AppSettings"/>.
/// </summary>
public static class FeatureGate
{
    /// <summary>Инструмент доступен: линейка — только с включённой функцией «Линейка».</summary>
    public static bool ToolAllowed(this AppSettings settings, ToolKind kind)
        => kind != ToolKind.Ruler || settings.IsFeatureOn(FeatureCatalog.Ruler);

    /// <summary>Действие завершения доступно: закрепление — с функцией «Закрепление», текст — с распознаванием.</summary>
    public static bool ActionAllowed(this AppSettings settings, CaptureAction action) => action switch
    {
        CaptureAction.Pin => settings.IsFeatureOn(FeatureCatalog.Pins),
        CaptureAction.CopyText => settings.IsFeatureOn(FeatureCatalog.Ocr),
        _ => true,
    };

    /// <summary>
    /// Проверка выделения на личные данные: своя функция и распознавание текста, без которого читать нечем.
    /// </summary>
    public static bool PrivacyCheckAllowed(this AppSettings settings)
        => settings.IsFeatureOn(FeatureCatalog.Privacy) && settings.IsFeatureOn(FeatureCatalog.Ocr);

    /// <summary>
    /// Enter копирует снимок: по настройке или всегда, если истории нет (иначе снимок пропал бы бесследно).
    /// Сохранённое значение <see cref="AppSettings.CopyOnCapture"/> не меняется — включили историю, и оно снова в силе.
    /// </summary>
    public static bool CopyOnEnter(this AppSettings settings)
        => settings.CopyOnCapture || !settings.IsFeatureOn(FeatureCatalog.History);

    /// <summary>
    /// Поля окна настроек, скрытые вместе со своей функцией, сохраняются без изменений (что бы ни было в скрытых
    /// галочках): без истории — папка, лимит, оригиналы и «Enter копирует», без закрепления — «Не захватывать закреплённые»,
    /// без распознавания текста — «Точное распознавание».
    /// Так выключение истории никогда не выглядит как выключение оригиналов и не предлагает их удалить.
    /// </summary>
    public static void KeepHiddenSettings(AppSettings updated, AppSettings current)
    {
        if (!updated.IsFeatureOn(FeatureCatalog.History))
        {
            updated.HistoryFolder = current.HistoryFolder;
            updated.HistoryLimit = current.HistoryLimit;
            updated.KeepOriginals = current.KeepOriginals;
            updated.CopyOnCapture = current.CopyOnCapture;
        }
        if (!updated.IsFeatureOn(FeatureCatalog.Pins)) updated.ExcludePinsFromCapture = current.ExcludePinsFromCapture;
        if (!updated.IsFeatureOn(FeatureCatalog.Ocr)) updated.PreciseOcr = current.PreciseOcr;
    }

    /// <summary>
    /// Новый список выключенных функций по галочкам (окно настроек, мастер). Строится от текущего: id, которых нет в
    /// <paramref name="choices"/> (в том числе неизвестные от более новой версии), остаются как были.
    /// </summary>
    public static List<string> MergeDisabled(IEnumerable<string> current, IEnumerable<KeyValuePair<string, bool>> choices)
    {
        var settings = new AppSettings { DisabledFeatures = [.. current] };
        foreach (var (id, on) in choices) settings.SetFeature(id, on);
        return settings.DisabledFeatures;
    }
}

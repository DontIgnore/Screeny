namespace Screeny.Features;

/// <summary>Отключаемая функция: id хранится в настройках и в метках списка изменений (<c>&lt;!-- feature: id --&gt;</c>).</summary>
/// <param name="TitleKey">Ключ названия в строках интерфейса (<see cref="Localization.Loc"/>).</param>
/// <param name="DescriptionKey">Ключ пояснения.</param>
public sealed record FeatureInfo(string Id, string TitleKey, string DescriptionKey)
{
    public string Title => Localization.Loc.T(TitleKey);
    public string Description => Localization.Loc.T(DescriptionKey);
}

/// <summary>
/// Функции, которые пользователь может выключить в мастере первого запуска и в настройках. Выключенная функция исчезает
/// целиком: нет кнопок, клавиш, пунктов меню и разделов настроек, её код не запускается, а пункты списка изменений про неё
/// не показываются. В настройках хранится список выключенных (<see cref="Settings.AppSettings.DisabledFeatures"/>),
/// поэтому новые функции по умолчанию включены.
/// </summary>
public static class FeatureCatalog
{
    public const string Ocr = "ocr";
    public const string History = "history";
    public const string Pins = "pins";
    public const string Beautify = "beautify";
    public const string Pipette = "pipette";
    public const string Ruler = "ruler";
    /// <summary>Предупреждение о личных данных в выделении; работает только вместе с <see cref="Ocr"/>.</summary>
    public const string Privacy = "privacy";
    /// <summary>Серия снимков по удержанию клавиши снимка.</summary>
    public const string Burst = "burst";

    public static IReadOnlyList<FeatureInfo> All { get; } =
    [
        new(Ocr, "Feature.Ocr.Title", "Feature.Ocr.Description"),
        new(History, "Feature.History.Title", "Feature.History.Description"),
        new(Pins, "Feature.Pins.Title", "Feature.Pins.Description"),
        new(Beautify, "Feature.Beautify.Title", "Feature.Beautify.Description"),
        new(Pipette, "Feature.Pipette.Title", "Feature.Pipette.Description"),
        new(Ruler, "Feature.Ruler.Title", "Feature.Ruler.Description"),
        new(Privacy, "Feature.Privacy.Title", "Feature.Privacy.Description"),
        new(Burst, "Feature.Burst.Title", "Feature.Burst.Description"),
    ];

    public static bool IsKnown(string id) => All.Any(f => f.Id == id);
}

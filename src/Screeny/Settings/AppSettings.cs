using System.Windows.Media;
using Screeny.Annotations;
using Screeny.Export;
using Screeny.Features;
using Screeny.Hotkeys;
using Screeny.Localization;
using Screeny.Logging;

namespace Screeny.Settings;

public sealed class AppSettings
{
    public const int MinHistoryLimit = 1, MaxHistoryLimit = 500;
    public const int MinThickness = 1, MaxThickness = 20;

    /// <summary>Версия формата файла настроек; <see cref="SettingsMigrator"/> поднимает старые файлы до неё.</summary>
    public const int CurrentVersion = 2;

    /// <summary>Версия формата. У файла без поля — 1 (до списка хоткеев).</summary>
    public int SettingsVersion { get; set; } = CurrentVersion;

    /// <summary>
    /// Хоткеи снимка: всегда ровно одна запись PrintScreen и одна WinShiftS (после <see cref="Normalize"/>), затем свои
    /// сочетания. Хотя бы одна запись включена. Запасной <c>Ctrl+Alt+S</c> здесь не хранится: его включает
    /// <see cref="HotkeyManager"/>, когда PrintScreen недоступен и ничего другого не работает.
    /// </summary>
    public List<HotkeyBinding> Hotkeys { get; set; } = DefaultHotkeys();

    public static List<HotkeyBinding> DefaultHotkeys() => [HotkeyBinding.PrintScreen(true), HotkeyBinding.WinShiftS(false)];
    public string HistoryFolder { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Screeny", "History");
    public int HistoryLimit { get; set; } = 50;
    public bool CopyOnCapture { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public string LastColor { get; set; } = "#FF3B30";
    public int LastThickness { get; set; } = 3;
    /// <summary>Скрывать закреплённые снимки от захвата экрана (SetWindowDisplayAffinity).</summary>
    public bool ExcludePinsFromCapture { get; set; } = false;
    /// <summary>
    /// Хранить рядом со снимком истории регион без пометок (<c>.orig.png</c>) для повторного редактирования.
    /// По умолчанию выключено: оригинал содержит то, что скрыто размытием и пикселизацией.
    /// </summary>
    public bool KeepOriginals { get; set; } = false;
    /// <summary>
    /// Прилипание рамки снимка к краям окон и контрастным границам, направляющие для фигур. По умолчанию включено;
    /// <c>Alt</c> во время перетаскивания временно отключает.
    /// </summary>
    public bool SnapEnabled { get; set; } = true;
    /// <summary>
    /// Проверять обновления на GitHub Releases. Снимки никуда не отправляются: в сеть Screeny ходит только за обновлениями
    /// и, по просьбе пользователя, за модулем точного распознавания (его загрузку эта настройка не запрещает).
    /// По умолчанию включено; показывается в настройках и в мастере первого запуска.
    /// </summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>
    /// Точное распознавание текста (PaddleOCR из скачиваемого модуля, <see cref="Modules.OcrModule"/>). Согласие, а не функция
    /// каталога: по умолчанию выключено и включается только явным действием — загрузкой модуля или галочкой в настройках.
    /// Работает, только если модуль нужной версии установлен (<see cref="Modules.PreciseOcrRules.IsActive"/>).
    /// </summary>
    public bool PreciseOcr { get; set; } = false;
    /// <summary>
    /// Версия мастера первого запуска, которую пользователь уже прошёл (0 — ни разу). Мастер показывается, если
    /// значение меньше текущей версии мастера; так он появляется и в портативной версии, и после установки.
    /// </summary>
    public int SetupWizardVersion { get; set; } = 0;
    /// <summary>
    /// Id мешающих программ (<c>Migration.ConflictCatalog</c>), которые пользователь уже видел в мастере или в уведомлении.
    /// При запуске уведомление показывается только о новых — тех, кого здесь нет.
    /// </summary>
    public List<string> KnownConflicts { get; set; } = new();
    /// <summary>
    /// Id выключенных функций (<see cref="FeatureCatalog"/>). Хранятся выключенные, а не включённые: функция из новой версии
    /// по умолчанию включена. Неизвестные id (от более новой Screeny) сохраняются как есть и ни на что не влияют.
    /// </summary>
    public List<string> DisabledFeatures { get; set; } = new();
    /// <summary>
    /// Язык интерфейса: <see cref="Loc.Auto"/> (язык Windows, по умолчанию и у старых файлов без поля) или код из
    /// <see cref="Loc.Supported"/>. Имя культуры («ru-RU») читается как язык, который для неё выбрал бы «auto»,
    /// неизвестное значение — как «auto».
    /// </summary>
    public string Language { get; set; } = Loc.Auto;

    public bool IsFeatureOn(string id) => !DisabledFeatures.Contains(id);

    public void SetFeature(string id, bool on)
    {
        DisabledFeatures.Remove(id);
        if (!on) DisabledFeatures.Add(id);
    }

    // Последние значения полосы параметров оверлея.
    public const int MinFillOpacity = 10, MaxFillOpacity = 100;
    /// <summary>Обводка у прямоугольника и эллипса. Хотя бы одно из обводки и заливки включено.</summary>
    public bool LastStrokeEnabled { get; set; } = true;
    public bool LastFillEnabled { get; set; } = false;
    /// <summary>Прозрачность заливки в процентах (цвет заливки — цвет палитры).</summary>
    public int LastFillOpacity { get; set; } = 40;
    public ArrowStyle LastArrowStyle { get; set; } = ArrowStyle.Normal;
    public bool LastTextBackground { get; set; } = false;
    public TextAlign LastTextAlign { get; set; } = TextAlign.Left;

    // Оформление (beautify): по умолчанию выключено, состояние и параметры запоминаются.
    public bool BeautifyEnabled { get; set; } = false;
    public BeautifyPreset BeautifyPreset { get; set; } = BeautifyOptions.Default.Preset;
    public BeautifyPadding BeautifyPadding { get; set; } = BeautifyOptions.Default.Padding;
    /// <summary>Скругление углов снимка в пикселях: 0, 8 или 16.</summary>
    public int BeautifyRadius { get; set; } = BeautifyOptions.Default.Radius;
    public bool BeautifyShadow { get; set; } = BeautifyOptions.Default.Shadow;

    /// <summary>
    /// Заменяет недопустимые значения (null, пустые строки, относительную папку, нераспознаваемый цвет или хоткей,
    /// числа вне диапазона) значениями по умолчанию. Нужен после чтения файла, который мог быть изменён вручную.
    /// </summary>
    public AppSettings Normalize()
    {
        var d = new AppSettings();
        Hotkeys = NormalizeHotkeys(Hotkeys);
        if (!IsValidFolder(HistoryFolder)) HistoryFolder = d.HistoryFolder;
        if (!IsValidColor(LastColor)) LastColor = d.LastColor;
        HistoryLimit = Math.Clamp(HistoryLimit, MinHistoryLimit, MaxHistoryLimit);
        LastThickness = Math.Clamp(LastThickness, MinThickness, MaxThickness);
        LastFillOpacity = Math.Clamp(LastFillOpacity, MinFillOpacity, MaxFillOpacity);
        if (!Enum.IsDefined(LastArrowStyle)) LastArrowStyle = d.LastArrowStyle;
        if (!Enum.IsDefined(LastTextAlign)) LastTextAlign = d.LastTextAlign;
        if (!LastStrokeEnabled && !LastFillEnabled) LastStrokeEnabled = true; // невидимая фигура
        if (!Enum.IsDefined(BeautifyPreset)) BeautifyPreset = d.BeautifyPreset;
        if (!Enum.IsDefined(BeautifyPadding)) BeautifyPadding = d.BeautifyPadding;
        BeautifyRadius = BeautifyOptions.SnapRadius(BeautifyRadius);
        if (SetupWizardVersion < 0) SetupWizardVersion = 0;
        Language = Loc.NormalizeSetting(Language);
        KnownConflicts = (KnownConflicts ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.Ordinal).ToList();
        DisabledFeatures = (DisabledFeatures ?? []).Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim().ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToList();
        // KeepOriginals — bool без недопустимых значений: отсутствующее в файле поле остаётся false (по умолчанию).
        // SnapEnabled — так же: отсутствующее поле (настройки старой версии) остаётся true.
        return this;
    }

    /// <summary>
    /// Приводит список хоткеев к виду, на который рассчитывают окно настроек, мастер и <see cref="HotkeyManager"/>:
    /// первая запись PrintScreen, вторая WinShiftS (повторы схлопываются, «включено» — если включена хоть одна копия),
    /// затем свои сочетания в канонической записи без повторов; нераспознанные и недопустимые (<see cref="HotkeyRules"/>)
    /// выбрасываются с записью в журнал. Свой «PrintScreen» превращается в запись PrintScreen. Если ничего не включено —
    /// включается PrintScreen.
    /// </summary>
    public static List<HotkeyBinding> NormalizeHotkeys(IEnumerable<HotkeyBinding?>? source)
    {
        bool printScreen = false, winShiftS = false;
        var customs = new List<HotkeyBinding>();
        var seen = new HashSet<HotkeySpec>();
        foreach (var b in source ?? [])
        {
            if (b is null) continue;
            switch (b.Kind)
            {
                case HotkeyKind.PrintScreen: printScreen |= b.Enabled; break;
                case HotkeyKind.WinShiftS: winShiftS |= b.Enabled; break;
                case HotkeyKind.Custom:
                    if (b.Spec is null || !HotkeyParser.TryParse(b.Spec, out var spec) || HotkeyRules.Validate(spec) is not null)
                    {
                        Log.Error($"Settings: dropped invalid custom hotkey \"{b.Spec}\"");
                        break;
                    }
                    if (spec == HotkeySpec.PrintScreen) { printScreen |= b.Enabled; break; }
                    if (!seen.Add(spec)) break; // повтор: первая запись важнее
                    customs.Add(HotkeyBinding.Custom(spec.ToString(), b.Enabled));
                    break;
                default:
                    Log.Error($"Settings: dropped hotkey of unknown kind {(int)b.Kind}");
                    break;
            }
        }
        if (!printScreen && !winShiftS && !customs.Any(c => c.Enabled)) printScreen = true;
        return [HotkeyBinding.PrintScreen(printScreen), HotkeyBinding.WinShiftS(winShiftS), .. customs];
    }

    /// <summary>
    /// Включает или выключает PrintScreen / WinShiftS (для мастера первого запуска). Свои сочетания правятся через
    /// <see cref="Hotkeys"/>. Как и <see cref="NormalizeHotkeys"/>, не оставляет все хоткеи выключенными.
    /// </summary>
    public void SetHotkeyEnabled(HotkeyKind kind, bool enabled)
    {
        var entry = kind switch
        {
            HotkeyKind.PrintScreen => HotkeyBinding.PrintScreen(enabled),
            HotkeyKind.WinShiftS => HotkeyBinding.WinShiftS(enabled),
            _ => throw new ArgumentException("Custom hotkeys are edited through Hotkeys", nameof(kind)),
        };
        Hotkeys = NormalizeHotkeys(Hotkeys.Where(b => b.Kind != kind).Prepend(entry));
    }

    public bool IsHotkeyEnabled(HotkeyKind kind) => Hotkeys.Any(b => b.Kind == kind && b.Enabled);

    private static bool IsValidFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try { return Path.IsPathFullyQualified(path) && path.IndexOfAny(Path.GetInvalidPathChars()) < 0 && Path.GetFullPath(path).Length > 0; }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private static bool IsValidColor(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try { return ColorConverter.ConvertFromString(text) is Color; }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException) { return false; }
    }
}

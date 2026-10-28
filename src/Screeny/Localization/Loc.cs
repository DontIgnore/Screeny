using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows;
using System.Windows.Data;

namespace Screeny.Localization;

/// <summary>
/// Строки интерфейса из <c>Localization/Strings*.resx</c> (английский — нейтральный язык, остальные — сопутствующие сборки).
/// Язык меняется на лету: XAML берёт строки через <see cref="LocExtension"/> (привязка к индексатору <see cref="Instance"/>),
/// код — через <see cref="T"/> / <see cref="F"/>, а текст, собранный в коде из нескольких частей, пересобирается по
/// <see cref="Changed"/>. Культуры потоков не меняются: от них зависят форматы чисел и дат и определение «как в системе».
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    /// <summary>Значение настройки <see cref="Settings.AppSettings.Language"/>: язык Windows.</summary>
    public const string Auto = "auto";
    public const string Fallback = "en";

    /// <summary>Поддерживаемые языки в порядке списка в настройках.</summary>
    public static IReadOnlyList<string> Supported { get; } = ["en", "ru", "es", "zh-Hans", "ja"];

    private static readonly ResourceManager Resources = new("Screeny.Localization.Strings", typeof(Loc).Assembly);

    public static Loc Instance { get; } = new();

    /// <summary>Язык интерфейса Windows на момент запуска — основа для <see cref="Auto"/>.</summary>
    public static CultureInfo SystemCulture { get; } = CultureInfo.CurrentUICulture;

    /// <summary>Текущий язык строк (одна из <see cref="Supported"/>).</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo(Resolve(Auto, SystemCulture));

    /// <summary>Язык сменился (после обновления привязок XAML).</summary>
    public static event Action? Changed;

    /// <summary>Сколько обработчиков подписано на <see cref="Changed"/> — тесты проверяют, что окна и панели отписываются.</summary>
    public static int ChangedSubscriberCount => Changed?.GetInvocationList().Length ?? 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    private Loc() { }

    /// <summary>Для привязок XAML: <c>{Binding [Key], Source={x:Static l:Loc.Instance}}</c>.</summary>
    public string this[string key] => T(key);

    /// <summary>Строка по ключу на текущем языке; если ключа нет — сам ключ (тесты следят, чтобы так не было).</summary>
    public static string T(string key) => Resources.GetString(key, Culture) ?? key;

    /// <summary>Строка с подстановками <c>{0}</c>… Числа форматируются по региональным настройкам пользователя.</summary>
    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

    /// <summary>Строка на заданном языке — для тестов и списка языков.</summary>
    public static string? Get(string key, CultureInfo culture) => Resources.GetString(key, culture);

    /// <summary>Набор строк одного языка без подстановки из нейтрального (null — сопутствующей сборки нет).</summary>
    public static ResourceSet? GetResourceSet(CultureInfo culture) => Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);

    /// <summary>Применяет настройку языка; привязки обновляются, затем срабатывает <see cref="Changed"/>.</summary>
    public static void Apply(string? setting)
    {
        var culture = CultureInfo.GetCultureInfo(Resolve(setting, SystemCulture));
        if (culture.Name == Culture.Name) return;
        Culture = culture;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs(Binding.IndexerName));
        Changed?.Invoke();
    }

    /// <summary>
    /// Код языка для настройки: поддерживаемый код — он сам (без учёта регистра), иначе (<see cref="Auto"/>, пусто,
    /// неизвестное значение) — язык Windows: сама культура или ближайший родитель из <see cref="Supported"/>
    /// (ru-RU → ru, zh-CN и zh-SG → zh-Hans через родителя zh-Hans), иначе английский. zh-TW и zh-HK — через zh-Hant,
    /// которого в списке нет, поэтому английский.
    /// </summary>
    public static string Resolve(string? setting, CultureInfo system)
    {
        if (Canonical(setting) is { } explicitCode) return explicitCode;
        for (var c = system; !string.IsNullOrEmpty(c.Name); c = c.Parent)
            if (Supported.FirstOrDefault(s => string.Equals(s, c.Name, StringComparison.OrdinalIgnoreCase)) is { } match)
                return match;
        return Fallback;
    }

    /// <summary>
    /// Поддерживаемый язык для имени культуры Windows: сама культура или ближайший родитель из <see cref="Supported"/>
    /// («en-US» → en, «zh-CN» → zh-Hans); null — имя не культура или в цепочке нет поддерживаемого («zh», «zh-TW», «de»).
    /// </summary>
    public static string? MatchCulture(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        CultureInfo culture;
        try { culture = CultureInfo.GetCultureInfo(name.Trim(), predefinedOnly: true); }
        catch (CultureNotFoundException) { return null; }
        for (var c = culture; !string.IsNullOrEmpty(c.Name); c = c.Parent)
            if (Supported.FirstOrDefault(s => string.Equals(s, c.Name, StringComparison.OrdinalIgnoreCase)) is { } match) return match;
        return null;
    }

    /// <summary>
    /// Значение настройки после чтения файла: поддерживаемый код как есть (в канонической записи), имя существующей
    /// культуры — язык, который для неё выбрал бы <see cref="Auto"/> («ru-RU» → ru, «zh» и «de» → en), остальное — <see cref="Auto"/>.
    /// </summary>
    public static string NormalizeSetting(string? value)
    {
        if (Canonical(value) is { } code) return code;
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), Auto, StringComparison.OrdinalIgnoreCase)) return Auto;
        try { return Resolve(Auto, CultureInfo.GetCultureInfo(value.Trim(), predefinedOnly: true)); }
        catch (CultureNotFoundException) { return Auto; }
    }

    /// <summary>Код из <see cref="Supported"/> в канонической записи или null (в том числе для <see cref="Auto"/>).</summary>
    public static string? Canonical(string? code)
        => Supported.FirstOrDefault(s => string.Equals(s, code?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Название языка на нём самом — так его ищут в списке, не зная текущего языка интерфейса.</summary>
    public static string NativeName(string code) => code switch
    {
        "ru" => "Русский",
        "es" => "Español",
        "zh-Hans" => "简体中文",
        "ja" => "日本語",
        _ => "English",
    };

    /// <summary>
    /// Шрифт интерфейса (ресурс <c>Font.UI</c>) для текущего языка: Segoe UI и для японского и китайского — шрифт Windows
    /// с иероглифами этого языка, чтобы WPF не подставил начертания другого языка.
    /// </summary>
    public static string UiFontFamily => Culture.Name switch
    {
        "ja" => "Segoe UI Variable Text, Segoe UI, Yu Gothic UI, Meiryo UI",
        "zh-Hans" => "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Microsoft YaHei",
        _ => "Segoe UI Variable Text, Segoe UI",
    };

    /// <summary>Разделитель перечисления слов в тексте: «, », у японского и китайского — «、».</summary>
    public static string ListSeparator => Culture.Name is "ja" or "zh-Hans" ? "、" : ", ";

    /// <summary>Привязка к строке для элементов, собранных в коде: обновляется при смене языка.</summary>
    public static Binding Bind(string key) => new($"[{key}]") { Source = Instance, Mode = BindingMode.OneWay };

    public static void Bind(DependencyObject target, DependencyProperty property, string key)
        => BindingOperations.SetBinding(target, property, Bind(key));
}

using System.ComponentModel;
using System.Globalization;
using System.Resources;
using System.Windows;
using System.Windows.Data;

namespace Screeny.Localization;

/// <summary>
/// UI strings from <c>Localization/Strings*.resx</c> (English is neutral, others satellite assemblies).
/// Language changes on the fly: XAML takes strings via <see cref="LocExtension"/> (binding to indexer <see cref="Instance"/>),
/// code via <see cref="T"/> / <see cref="F"/>, and text built from parts in code is rebuilt on
/// <see cref="Changed"/>. Thread cultures are not changed: number/date formats and “as system” depend on them.
/// </summary>
public sealed class Loc : INotifyPropertyChanged
{
    /// <summary>Value of <see cref="Settings.AppSettings.Language"/>: Windows language.</summary>
    public const string Auto = "auto";
    public const string Fallback = "en";

    /// <summary>Supported languages in settings list order.</summary>
    public static IReadOnlyList<string> Supported { get; } = ["en", "ru", "es", "zh-Hans", "ja"];

    private static readonly ResourceManager Resources = new("Screeny.Localization.Strings", typeof(Loc).Assembly);

    public static Loc Instance { get; } = new();

    /// <summary>Windows UI language at startup — base for <see cref="Auto"/>.</summary>
    public static CultureInfo SystemCulture { get; } = CultureInfo.CurrentUICulture;

    /// <summary>Current strings language (one of <see cref="Supported"/>).</summary>
    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo(Resolve(Auto, SystemCulture));

    /// <summary>Language changed (after XAML bindings updated).</summary>
    public static event Action? Changed;

    /// <summary>How many handlers subscribed to <see cref="Changed"/> — tests ensure windows/panels unsubscribe.</summary>
    public static int ChangedSubscriberCount => Changed?.GetInvocationList().Length ?? 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    private Loc() { }

    /// <summary>For XAML bindings: <c>{Binding [Key], Source={x:Static l:Loc.Instance}}</c>.</summary>
    public string this[string key] => T(key);

    /// <summary>String by key in current language; missing key — key itself (tests prevent).</summary>
    public static string T(string key) => Resources.GetString(key, Culture) ?? key;

    /// <summary>String with <c>{0}</c> substitutions. Numbers formatted per user locale.</summary>
    public static string F(string key, params object?[] args) => string.Format(CultureInfo.CurrentCulture, T(key), args);

    /// <summary>String in given language — for tests and language list.</summary>
    public static string? Get(string key, CultureInfo culture) => Resources.GetString(key, culture);

    /// <summary>Set of strings of one language without neutral fallback (null — no satellite assembly).</summary>
    public static ResourceSet? GetResourceSet(CultureInfo culture) => Resources.GetResourceSet(culture, createIfNotExists: true, tryParents: false);

    /// <summary>Applies language setting; bindings update, then <see cref="Changed"/> fires.</summary>
    public static void Apply(string? setting)
    {
        var culture = CultureInfo.GetCultureInfo(Resolve(setting, SystemCulture));
        if (culture.Name == Culture.Name) return;
        Culture = culture;
        Instance.PropertyChanged?.Invoke(Instance, new PropertyChangedEventArgs(Binding.IndexerName));
        Changed?.Invoke();
    }

    /// <summary>
    /// Language code for setting: supported code — as-is (case-insensitive), else (<see cref="Auto"/>, empty,
    /// unknown) — Windows language: culture itself or nearest parent from <see cref="Supported"/>
    /// (ru-RU → ru, zh-CN and zh-SG → zh-Hans via parent zh-Hans), else English. zh-TW and zh-HK — via zh-Hant,
    /// which is not in list, so English.
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
    /// Supported language for Windows culture name: culture itself or nearest parent from <see cref="Supported"/>
    /// (“en-US” → en, “zh-CN” → zh-Hans); null — name not culture or no supported in chain (“zh”, “zh-TW”, “de”).
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
    /// Setting value after file read: supported code as-is (canonical), existing culture name — language Auto would choose
    /// culture maps to the language <see cref="Auto"/> would choose for it (ru-RU → ru, zh and de → en); otherwise <see cref="Auto"/>.
    /// </summary>
    public static string NormalizeSetting(string? value)
    {
        if (Canonical(value) is { } code) return code;
        if (string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), Auto, StringComparison.OrdinalIgnoreCase)) return Auto;
        try { return Resolve(Auto, CultureInfo.GetCultureInfo(value.Trim(), predefinedOnly: true)); }
        catch (CultureNotFoundException) { return Auto; }
    }

    /// <summary>Code from <see cref="Supported"/> in canonical form or null (incl. <see cref="Auto"/>).</summary>
    public static string? Canonical(string? code)
        => Supported.FirstOrDefault(s => string.Equals(s, code?.Trim(), StringComparison.OrdinalIgnoreCase));

    /// <summary>Language name in itself — searched in list without knowing current UI language.</summary>
    public static string NativeName(string code) => code switch
    {
        "ru" => "Русский",
        "es" => "Español",
        "zh-Hans" => "简体中文",
        "ja" => "日本語",
        _ => "English",
    };

    /// <summary>
    /// UI font (resource <c>Font.UI</c>) for current language: Segoe UI, and for Japanese/Chinese Windows font
    /// with CJK glyphs of that language, so WPF doesn't substitute another language's glyphs.
    /// </summary>
    public static string UiFontFamily => Culture.Name switch
    {
        "ja" => "Segoe UI Variable Text, Segoe UI, Yu Gothic UI, Meiryo UI",
        "zh-Hans" => "Segoe UI Variable Text, Segoe UI, Microsoft YaHei UI, Microsoft YaHei",
        _ => "Segoe UI Variable Text, Segoe UI",
    };

    /// <summary>Word list separator: “, ”, for Japanese/Chinese “、”.</summary>
    public static string ListSeparator => Culture.Name is "ja" or "zh-Hans" ? "、" : ", ";

    /// <summary>Binding to string for code-built elements: updates on language change.</summary>
    public static Binding Bind(string key) => new($"[{key}]") { Source = Instance, Mode = BindingMode.OneWay };

    public static void Bind(DependencyObject target, DependencyProperty property, string key)
        => BindingOperations.SetBinding(target, property, Bind(key));
}

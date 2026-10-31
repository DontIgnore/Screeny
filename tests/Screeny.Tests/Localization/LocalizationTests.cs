using System.Collections;
using System.Globalization;
using System.Resources;
using System.Text.RegularExpressions;
using Screeny.Localization;
using Screeny.Settings;
using Screeny.Tests.Updates;

namespace Screeny.Tests.Localization;

/// <summary>UI strings: translation completeness, substitutions and keys referenced by code.</summary>
public partial class LocalizationTests
{
    public static TheoryData<string> Translations => new(Loc.Supported.Where(l => l != Loc.Fallback));

    private static Dictionary<string, string> Strings(CultureInfo culture)
    {
        var set = Loc.GetResourceSet(culture);
        Assert.True(set is not null, $"No resources for {culture.Name}: the satellite assembly is missing");
        return set!.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => (string)e.Value!);
    }

    private static Dictionary<string, string> English => Strings(CultureInfo.InvariantCulture);

    [Fact]
    public void Supported_AreTheFiveSiteLanguages()
        => Assert.Equal(["en", "ru", "es", "zh-Hans", "ja"], Loc.Supported);

    [Theory]
    [MemberData(nameof(Translations))]
    public void EveryKey_ExistsInEveryLanguage(string language)
    {
        var translated = Strings(CultureInfo.GetCultureInfo(language));
        Assert.Empty(English.Keys.Except(translated.Keys)); // untranslated
        Assert.Empty(translated.Keys.Except(English.Keys)); // extra: key removed or renamed
    }

    [Theory]
    [MemberData(nameof(Translations))]
    [InlineData("en")]
    public void NoEmptyValues(string language)
    {
        var strings = language == Loc.Fallback ? English : Strings(CultureInfo.GetCultureInfo(language));
        Assert.Empty(strings.Where(s => string.IsNullOrWhiteSpace(s.Value)).Select(s => s.Key));
    }

    [GeneratedRegex(@"\{(\d+)(?:[,:][^}]*)?\}")]
    private static partial Regex PlaceholderRx();

    private static string Placeholders(string text)
        => string.Join(",", PlaceholderRx().Matches(text).Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order());

    [Theory]
    [MemberData(nameof(Translations))]
    public void Placeholders_MatchEnglish(string language)
    {
        var translated = Strings(CultureInfo.GetCultureInfo(language));
        var mismatched = English.Where(e => translated.TryGetValue(e.Key, out var t) && Placeholders(t) != Placeholders(e.Value))
            .Select(e => $"{e.Key}: «{e.Value}» / «{translated[e.Key]}»");
        Assert.Empty(mismatched);
    }

    [Fact]
    public void FormatStrings_FormatWithTheirPlaceholders()
    {
        // Each substitution string formats without exception (no lone “{”, numbers in 0…n−1 range).
        foreach (var language in Loc.Supported)
        {
            var strings = language == Loc.Fallback ? English : Strings(CultureInfo.GetCultureInfo(language));
            foreach (var (key, text) in strings.Where(s => PlaceholderRx().IsMatch(s.Value)))
            {
                int count = PlaceholderRx().Matches(text).Max(m => int.Parse(m.Groups[1].Value)) + 1;
                var ex = Record.Exception(() => string.Format(CultureInfo.InvariantCulture, text, Enumerable.Repeat<object>("x", count).ToArray()));
                Assert.True(ex is null, $"{language} {key}: {ex?.Message}");
            }
        }
    }

    [GeneratedRegex(@"\{l:Loc\s+(?:Key=)?([\w.]+)\s*\}")]
    private static partial Regex XamlKeyRx();

    [GeneratedRegex("(?<!cref=)\"([A-Z][A-Za-z]*)\\.([A-Z][\\w.]*)\"")]
    private static partial Regex CodeKeyRx();

    /// <summary>
    /// Keys referenced by XAML (<c>{l:Loc …}</c>) and code (string literals like “Section.Name” with section from
    /// UI strings) exist in English strings — typo would show user the key itself.
    /// </summary>
    [Fact]
    public void KeysUsedInSource_Exist()
    {
        var english = English;
        var sections = english.Keys.Select(k => k[..k.IndexOf('.')]).ToHashSet();
        var root = ReleaseNotesTests.RepoFile("src", "Screeny");
        var missing = new List<string>();
        int found = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".xaml", StringComparison.Ordinal) || f.EndsWith(".cs", StringComparison.Ordinal))
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            var keys = XamlKeyRx().Matches(text).Select(m => m.Groups[1].Value)
                .Concat(CodeKeyRx().Matches(text).Where(m => sections.Contains(m.Groups[1].Value)).Select(m => m.Groups[1].Value + "." + m.Groups[2].Value));
            foreach (var key in keys)
            {
                found++;
                if (!english.ContainsKey(key)) missing.Add($"{Path.GetFileName(file)}: {key}");
            }
        }
        Assert.True(found > 300, $"only {found} key references found: the scan no longer sees the code");
        Assert.Empty(missing);
    }

    [Theory]
    [InlineData("ru-RU", "ru")]
    [InlineData("ru", "ru")]
    [InlineData("ru-UA", "ru")]
    [InlineData("en-US", "en")]
    [InlineData("en-GB", "en")]
    [InlineData("es-ES", "es")]
    [InlineData("es-MX", "es")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("zh-Hans", "zh-Hans")]
    [InlineData("zh-TW", "en")]
    [InlineData("zh-HK", "en")]
    [InlineData("zh-Hant", "en")]
    [InlineData("ja-JP", "ja")]
    [InlineData("de-DE", "en")]
    [InlineData("uk-UA", "en")]
    [InlineData("pt-BR", "en")]
    public void Auto_FollowsWindowsDisplayLanguage(string windows, string expected)
        => Assert.Equal(expected, Loc.Resolve(Loc.Auto, CultureInfo.GetCultureInfo(windows)));

    [Fact]
    public void Auto_InvariantCulture_IsEnglish()
        => Assert.Equal("en", Loc.Resolve(Loc.Auto, CultureInfo.InvariantCulture));

    [Theory]
    [InlineData("ja", "ja")]
    [InlineData("JA", "ja")]
    [InlineData(" zh-hans ", "zh-Hans")]
    [InlineData("ru", "ru")]
    [InlineData("auto", "es")]
    [InlineData("", "es")]
    [InlineData(null, "es")]
    [InlineData("de", "es")]
    [InlineData("zh-Hant", "es")]
    public void Explicit_Language_WinsOverWindows(string? setting, string expected)
        => Assert.Equal(expected, Loc.Resolve(setting, CultureInfo.GetCultureInfo("es-AR")));

    [Theory]
    [InlineData("ja", "ja")]
    [InlineData("ZH-HANS", "zh-Hans")]
    [InlineData("auto", "auto")]
    [InlineData("AUTO", "auto")]
    [InlineData("klingon", "auto")]
    [InlineData("", "auto")]
    [InlineData(null, "auto")]
    [InlineData("ru-RU", "ru")]         // culture name — as Auto would choose for it
    [InlineData("en-US", "en")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh", "en")]
    [InlineData("zh-TW", "en")]
    [InlineData("de-DE", "en")]
    public void Normalize_KeepsKnownLanguage_OtherwiseAuto(string? value, string expected)
        => Assert.Equal(expected, new AppSettings { Language = value! }.Normalize().Language);

    [Fact]
    public void NewSettings_LanguageIsAuto() => Assert.Equal(Loc.Auto, new AppSettings().Language);

    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("zh-CN", "zh-Hans")]
    [InlineData("zh-SG", "zh-Hans")]
    [InlineData("ru", "ru")]
    [InlineData("zh", null)]
    [InlineData("zh-TW", null)]
    [InlineData("de", null)]
    [InlineData("not a culture", null)]
    [InlineData(null, null)]
    public void MatchCulture_FindsSupportedLanguageInParents(string? name, string? expected)
        => Assert.Equal(expected, Loc.MatchCulture(name));

    [Fact]
    public void NativeNames_AreInTheirOwnLanguage()
        => Assert.Equal(["English", "Русский", "Español", "简体中文", "日本語"], Loc.Supported.Select(Loc.NativeName));

    [Fact]
    public void ResourceManager_FallsBackToEnglishForUnknownCulture()
        => Assert.Equal(English["Common.Cancel"], Loc.Get("Common.Cancel", CultureInfo.GetCultureInfo("de-DE")));
}

/// <summary>Old settings file (without “Language” field) read with language “as system”.</summary>
public class LanguageSettingMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScreenyTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (DirectoryNotFoundException) { }
    }

    private AppSettings Load(string json)
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "settings.json");
        File.WriteAllText(path, json);
        return new SettingsStore(path).Load();
    }

    [Fact]
    public void OldFileWithoutLanguage_LoadsAsAuto()
        => Assert.Equal(Loc.Auto, Load("""{ "SettingsVersion": 2, "HistoryLimit": 10 }""").Language);

    [Fact]
    public void V1FileWithoutLanguage_LoadsAsAuto()
        => Assert.Equal(Loc.Auto, Load("""{ "Hotkey": "PrintScreen", "HistoryLimit": 10 }""").Language);

    [Fact]
    public void SavedLanguage_IsKept() => Assert.Equal("zh-Hans", Load("""{ "SettingsVersion": 2, "Language": "zh-hans" }""").Language);

    [Fact]
    public void UnknownLanguage_LoadsAsAuto_WithoutResettingTheRest()
    {
        var s = Load("""{ "SettingsVersion": 2, "Language": "not-a-language", "HistoryLimit": 10 }""");
        Assert.Equal((Loc.Auto, 10), (s.Language, s.HistoryLimit));
    }

    [Theory]
    [InlineData("5")]
    [InlineData("null")]
    [InlineData("[\"ru\"]")]
    public void NonStringLanguage_DroppedWithoutResettingTheRest(string value)
    {
        var s = Load($$"""{ "SettingsVersion": 2, "Language": {{value}}, "HistoryLimit": 10 }""");
        Assert.Equal((Loc.Auto, 10), (s.Language, s.HistoryLimit));
        Assert.Empty(Directory.GetFiles(_dir, "*.corrupt-*"));
    }
}

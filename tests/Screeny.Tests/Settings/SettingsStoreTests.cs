using System.Text.Json;
using Screeny.Annotations;
using Screeny.Export;
using Screeny.Hotkeys;
using Screeny.Settings;

namespace Screeny.Tests.Settings;

public class SettingsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ScreenyTests", Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "settings.json");

    private static readonly HotkeyBinding[] DefaultHotkeys = [HotkeyBinding.PrintScreen(true), HotkeyBinding.WinShiftS(false)];

    private AppSettings LoadJson(string json)
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, json);
        return new SettingsStore(FilePath).Load();
    }

    [Fact]
    public void Load_WhenFileMissing_ReturnsDefaults()
    {
        var store = new SettingsStore(FilePath);
        var s = store.Load();
        Assert.Equal(DefaultHotkeys, s.Hotkeys);
        Assert.Equal(AppSettings.CurrentVersion, s.SettingsVersion);
        Assert.Equal(50, s.HistoryLimit);
        Assert.True(s.CopyOnCapture);
        Assert.False(s.StartWithWindows);
        Assert.Equal("#FF3B30", s.LastColor);
        Assert.Equal(3, s.LastThickness);
        Assert.False(s.ExcludePinsFromCapture); // pinned screenshots sometimes need removal
        Assert.False(s.KeepOriginals);          // original stores what blur hid
        Assert.EndsWith(Path.Combine("Screeny", "History"), s.HistoryFolder);
    }

    [Fact]
    public void Save_ThenLoad_RoundTripsAllProperties()
    {
        var store = new SettingsStore(FilePath);
        var s = new AppSettings
        {
            SettingsVersion = AppSettings.CurrentVersion,
            Hotkeys = [HotkeyBinding.PrintScreen(false), HotkeyBinding.WinShiftS(true), HotkeyBinding.Custom("Ctrl+Shift+X")],
            HistoryFolder = Path.Combine(_dir, "MyHistory"),
            HistoryLimit = 10,
            CopyOnCapture = false,
            StartWithWindows = true,
            LastColor = "#00FF00",
            LastThickness = 7,
            ExcludePinsFromCapture = true,
            KeepOriginals = true,
            LastStrokeEnabled = false,   // paired with fill: both disabled Normalize would fix
            LastFillEnabled = true,
            LastFillOpacity = 70,
            LastArrowStyle = ArrowStyle.Thick,
            LastTextBackground = true,
            LastTextAlign = TextAlign.Right,
            SnapEnabled = false,
            CheckForUpdates = false,
            PreciseOcr = true,
            SetupWizardVersion = 3,
            KnownConflicts = ["lightshot", "sharex"],
            DisabledFeatures = ["ocr", "history"],
            BeautifyEnabled = true,
            BeautifyPreset = BeautifyPreset.Mint,
            BeautifyPadding = BeautifyPadding.L,
            BeautifyRadius = 16,
            BeautifyShadow = false,
            Language = "ja",
            InstallId = "0f8fad5b-d9cb-469f-a165-70867728950e",
        };
        var defaults = new AppSettings();
        var props = typeof(AppSettings).GetProperties();
        Assert.Equal(29, props.Length); // new field — add here
        // Each field actually validated; saved file format version always current.
        foreach (var p in props.Where(p => p.Name != nameof(AppSettings.SettingsVersion))) Assert.NotEqual(p.GetValue(defaults), p.GetValue(s));

        store.Save(s);
        var loaded = store.Load();
        foreach (var p in props) Assert.Equal(p.GetValue(s), p.GetValue(loaded));
        Assert.Equal(["settings.json"], Directory.GetFiles(_dir).Select(Path.GetFileName)); // temp file doesn't remain
    }

    [Fact]
    public void Save_OverwritesExistingFile()
    {
        var store = new SettingsStore(FilePath);
        store.Save(new AppSettings { HistoryLimit = 10 });
        store.Save(new AppSettings { HistoryLimit = 20 });
        Assert.Equal(20, store.Load().HistoryLimit);
    }

    [Fact]
    public void Load_WhenFileCorrupt_ReturnsDefaults() => Assert.Equal(DefaultHotkeys, LoadJson("{ not json").Hotkeys);

    [Fact]
    public void Load_RootIsArray_TreatedAsCorrupt()
    {
        Assert.Equal(DefaultHotkeys, LoadJson("[1, 2]").Hotkeys);
        Assert.Single(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Load_CorruptFile_KeepsBackupCopy()
    {
        LoadJson("{ not json");
        var backup = Assert.Single(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
        Assert.Matches(@"^settings\.json\.corrupt-\d{8}-\d{6}$", Path.GetFileName(backup));
        Assert.Equal("{ not json", File.ReadAllText(backup));
        Assert.True(File.Exists(FilePath)); // original untouched, next Save overwrites
    }

    [Fact]
    public void Load_NullFields_ReturnsDefaults()
    {
        var s = LoadJson("""{ "SettingsVersion": 2, "Hotkeys": null, "HistoryFolder": null, "LastColor": null }""");
        var d = new AppSettings();
        Assert.Equal(DefaultHotkeys, s.Hotkeys);
        Assert.Equal(d.HistoryFolder, s.HistoryFolder);
        Assert.Equal(d.LastColor, s.LastColor);
    }

    [Fact]
    public void Load_EmptyStrings_ReturnDefaults()
    {
        var s = LoadJson("""{ "Hotkey": "", "FallbackHotkey": "  ", "HistoryFolder": "", "LastColor": "" }""");
        var d = new AppSettings();
        Assert.Equal(DefaultHotkeys, s.Hotkeys);
        Assert.Equal(d.HistoryFolder, s.HistoryFolder);
        Assert.Equal(d.LastColor, s.LastColor);
    }

    [Fact]
    public void Load_LiteralNull_ReturnsDefaults()
    {
        Assert.Equal(DefaultHotkeys, LoadJson("null").Hotkeys);
        Assert.Empty(Directory.GetFiles(_dir, "settings.json.corrupt-*"));
    }

    [Fact]
    public void Load_WrongTypes_ReadsNumbersFromStrings()
    {
        var s = LoadJson("""{ "HistoryLimit": "25", "LastThickness": "5", "Hotkey": "Ctrl+Shift+Q" }""");
        Assert.Equal(25, s.HistoryLimit);
        Assert.Equal(5, s.LastThickness);
        Assert.Equal(HotkeyBinding.Custom("Ctrl+Shift+Q"), s.Hotkeys[2]); // other fields not reset
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(100000, 500)]
    [InlineData(500, 500)]
    public void Load_HistoryLimitOutOfRange_IsClamped(int value, int expected)
        => Assert.Equal(expected, LoadJson($$"""{ "HistoryLimit": {{value}} }""").HistoryLimit);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(99, 20)]
    public void Load_ThicknessOutOfRange_IsClamped(int value, int expected)
        => Assert.Equal(expected, LoadJson($$"""{ "LastThickness": {{value}} }""").LastThickness);

    [Theory]
    [InlineData("History")]
    [InlineData(@"..\History")]
    [InlineData(@"C:History")]
    [InlineData(@"\History")]
    public void Load_RelativeHistoryFolder_FallsBackToDefault(string folder)
        => Assert.Equal(new AppSettings().HistoryFolder, LoadJson(JsonSerializer.Serialize(new { HistoryFolder = folder })).HistoryFolder);

    [Fact]
    public void Load_AbsoluteHistoryFolder_IsKept()
    {
        var folder = Path.Combine(_dir, "Shots");
        Assert.Equal(folder, LoadJson(JsonSerializer.Serialize(new { HistoryFolder = folder })).HistoryFolder);
    }

    [Theory]
    [InlineData("not-a-color")]
    [InlineData("#GG0000")]
    public void Load_InvalidColor_FallsBackToDefault(string color)
        => Assert.Equal("#FF3B30", LoadJson($$"""{ "LastColor": "{{color}}" }""").LastColor);

    [Theory]
    [InlineData("#00FF00")]
    [InlineData("Red")]
    public void Load_ValidColor_IsKept(string color)
        => Assert.Equal(color, LoadJson($$"""{ "LastColor": "{{color}}" }""").LastColor);

    [Theory]
    [InlineData("A")]
    [InlineData("Shift+A")]
    [InlineData("Win+Shift+S")]
    [InlineData("Ctrl+")]
    [InlineData("Nonsense+Q")]
    public void Load_V1InvalidHotkey_FallsBackToPrintScreen(string hotkey)
        => Assert.Equal(DefaultHotkeys, LoadJson($$"""{ "Hotkey": "{{hotkey}}", "FallbackHotkey": "{{hotkey}}" }""").Hotkeys);

    [Theory]
    [InlineData("F9")]
    [InlineData("Ctrl+Alt+P")]
    public void Load_V1ValidHotkey_BecomesCustomWithPrintScreenOff(string hotkey)
        => Assert.Equal([HotkeyBinding.PrintScreen(false), HotkeyBinding.WinShiftS(false), HotkeyBinding.Custom(hotkey)],
            LoadJson($$"""{ "Hotkey": "{{hotkey}}" }""").Hotkeys);

    [Fact]
    public void Load_V1File_IsMigratedResavedAndBackedUp()
    {
        const string v1 = """{ "Hotkey": "Ctrl+Shift+X", "FallbackHotkey": "Ctrl+Alt+S", "HistoryLimit": 12 }""";
        var s = LoadJson(v1);
        Assert.Equal(12, s.HistoryLimit);
        Assert.Equal(v1, File.ReadAllText(FilePath + ".v1.bak"));

        var saved = File.ReadAllText(FilePath);
        Assert.DoesNotContain("FallbackHotkey", saved);
        Assert.DoesNotContain("\"Hotkey\"", saved);
        Assert.Contains("\"SettingsVersion\": 2", saved);
        Assert.Contains("\"Kind\": \"Custom\"", saved); // entry type string, not number
        Assert.Contains("\"Spec\": \"Ctrl+Shift+X\"", saved);

        var again = new SettingsStore(FilePath).Load();
        Assert.Equal(s.Hotkeys, again.Hotkeys);
        Assert.Equal(saved, File.ReadAllText(FilePath)); // v2 not rewritten again
    }

    [Fact]
    public void Load_V2File_IsNotRewritten()
    {
        const string v2 = """{ "SettingsVersion": 2, "Hotkeys": [ { "Kind": "PrintScreen", "Enabled": true } ], "HistoryLimit": 7 }""";
        var s = LoadJson(v2);
        Assert.Equal(7, s.HistoryLimit);
        Assert.Equal(DefaultHotkeys, s.Hotkeys); // missing WinShiftS added in memory
        Assert.Equal(v2, File.ReadAllText(FilePath));
        Assert.Equal(["settings.json"], Directory.GetFiles(_dir).Select(Path.GetFileName));
    }

    [Fact]
    public void Load_V2BrokenHotkeyEntries_AreDroppedWithoutResettingOtherSettings()
    {
        var s = LoadJson("""
            { "SettingsVersion": 2, "HistoryLimit": 9, "Hotkeys": [
                { "Kind": "Teleport", "Enabled": true },
                { "Kind": "Custom", "Spec": 5, "Enabled": true },
                { "Kind": "Custom", "Spec": "Ctrl+Shift+Q", "Enabled": null },
                42,
                { "Kind": "WinShiftS", "Enabled": true }
            ] }
            """);
        Assert.Equal(9, s.HistoryLimit);
        Assert.Equal([HotkeyBinding.PrintScreen(false), HotkeyBinding.WinShiftS(true)], s.Hotkeys);
    }

    [Fact]
    public void Load_StyleDefaults()
    {
        var s = new SettingsStore(FilePath).Load();
        Assert.True(s.LastStrokeEnabled);
        Assert.False(s.LastFillEnabled);
        Assert.Equal(40, s.LastFillOpacity);
        Assert.Equal(ArrowStyle.Normal, s.LastArrowStyle);
        Assert.False(s.LastTextBackground);
        Assert.Equal(TextAlign.Left, s.LastTextAlign);
        Assert.True(s.SnapEnabled); // snap default enabled
    }

    [Fact]
    public void Load_OldFileWithoutNewFields_KeepsSnapOnAndLeftAlign()
    {
        var s = LoadJson("""{ "Hotkey": "PrintScreen", "LastTextBackground": true }""");
        Assert.True(s.SnapEnabled);
        Assert.Equal(TextAlign.Left, s.LastTextAlign);
    }

    [Theory]
    [InlineData(42)]
    [InlineData(-1)]
    public void Load_UnknownTextAlign_FallsBackToLeft(int value)
        => Assert.Equal(TextAlign.Left, LoadJson($$"""{ "LastTextAlign": {{value}} }""").LastTextAlign);

    [Fact]
    public void Normalize_KeepsValidTextAlignAndSnapOff()
    {
        var s = new AppSettings { LastTextAlign = TextAlign.Center, SnapEnabled = false }.Normalize();
        Assert.Equal(TextAlign.Center, s.LastTextAlign);
        Assert.False(s.SnapEnabled);
    }

    [Fact]
    public void Load_StrokeAndFillBothOff_TurnsStrokeOn()
    {
        var s = LoadJson("""{ "LastStrokeEnabled": false, "LastFillEnabled": false }""");
        Assert.True(s.LastStrokeEnabled);
        Assert.False(s.LastFillEnabled);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-5, 10)]
    [InlineData(250, 100)]
    [InlineData(60, 60)]
    public void Load_FillOpacityOutOfRange_IsClamped(int value, int expected)
        => Assert.Equal(expected, LoadJson($$"""{ "LastFillOpacity": {{value}} }""").LastFillOpacity);

    [Fact]
    public void Load_UnknownArrowStyle_FallsBackToNormal()
        => Assert.Equal(ArrowStyle.Normal, LoadJson("""{ "LastArrowStyle": 42 }""").LastArrowStyle);

    [Fact]
    public void Load_WhenFileLocked_ReturnsDefaults()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, """{ "HistoryLimit": 7 }""");
        using (new FileStream(FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal(50, new SettingsStore(FilePath).Load().HistoryLimit);
    }

    [Fact]
    public void Load_WhenPathIsDirectory_ReturnsDefaults()
    {
        Directory.CreateDirectory(FilePath); // settings.json is folder: File.Exists = false, path “no file”
        Assert.Equal(DefaultHotkeys, new SettingsStore(FilePath).Load().Hotkeys);
    }

    public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

    [Fact]
    public void InstallId_MissingOrNull_StaysNull_AndFileIsNotRewritten()
    {
        Assert.Null(LoadJson("""{ "SettingsVersion": 2 }""").InstallId);
        var json = """{ "SettingsVersion": 2, "InstallId": null }""";
        Assert.Null(LoadJson(json).InstallId);
        Assert.Equal(json, File.ReadAllText(FilePath)); // null — valid value, not reason to rewrite
    }

    [Theory]
    [InlineData("not-a-guid")]
    [InlineData("")]
    public void InstallId_Invalid_BecomesNull(string value)
        => Assert.Null(LoadJson($$"""{ "SettingsVersion": 2, "InstallId": "{{value}}" }""").InstallId);

    [Fact]
    public void InstallId_WrongType_IsDropped_OtherSettingsKept()
    {
        var s = LoadJson("""{ "SettingsVersion": 2, "InstallId": 42, "HistoryLimit": 7 }""");
        Assert.Null(s.InstallId);
        Assert.Equal(7, s.HistoryLimit);
    }

    [Fact]
    public void InstallId_IsNormalizedAndPersists()
    {
        var store = new SettingsStore(FilePath);
        Assert.Equal("0f8fad5b-d9cb-469f-a165-70867728950e",
            LoadJson("""{ "SettingsVersion": 2, "InstallId": "{0F8FAD5B-D9CB-469F-A165-70867728950E}" }""").InstallId);
        var s = store.Load();
        s.InstallId = Guid.NewGuid().ToString("D");
        store.Save(s);
        Assert.Equal(s.InstallId, store.Load().InstallId);
    }
}

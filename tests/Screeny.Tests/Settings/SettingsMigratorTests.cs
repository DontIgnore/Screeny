using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Screeny.Hotkeys;
using Screeny.Settings;

namespace Screeny.Tests.Settings;

public class SettingsMigratorTests
{
    private static (JsonObject Result, bool Changed) Migrate(string json) => SettingsMigrator.Migrate(JsonNode.Parse(json)!.AsObject());

    private static readonly JsonSerializerOptions Readable = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static string Hotkeys(JsonObject o) => o["Hotkeys"]!.ToJsonString(Readable);

    private static string Entries(params string[] entries) => "[" + string.Join(",", entries) + "]";
    private const string PsOn = """{"Kind":"PrintScreen","Enabled":true}""";
    private const string PsOff = """{"Kind":"PrintScreen","Enabled":false}""";
    private const string WssOff = """{"Kind":"WinShiftS","Enabled":false}""";

    [Fact]
    public void V1PrintScreen_BecomesEnabledPrintScreenEntry()
    {
        var (r, changed) = Migrate("""{ "Hotkey": "PrintScreen", "FallbackHotkey": "Ctrl+Alt+S", "HistoryLimit": 5 }""");
        Assert.True(changed);
        Assert.Equal(Entries(PsOn, WssOff), Hotkeys(r));
        Assert.Equal(2, (int)r["SettingsVersion"]!);
        Assert.False(r.ContainsKey("Hotkey"));
        Assert.False(r.ContainsKey("FallbackHotkey")); // fallback no longer configurable
        Assert.Equal(5, (int)r["HistoryLimit"]!);     // other fields untouched
    }

    [Theory]
    [InlineData("Ctrl+Shift+X")]
    [InlineData("F9")]
    public void V1CustomHotkey_DisablesPrintScreenAndAddsCustom(string hotkey)
    {
        var (r, changed) = Migrate($$"""{ "Hotkey": "{{hotkey}}" }""");
        Assert.True(changed);
        Assert.Equal(Entries(PsOff, WssOff, $$"""{"Kind":"Custom","Spec":"{{hotkey}}","Enabled":true}"""), Hotkeys(r));
    }

    [Theory]
    [InlineData("""{ }""")]
    [InlineData("""{ "Hotkey": null }""")]
    [InlineData("""{ "Hotkey": 123 }""")]
    [InlineData("""{ "Hotkey": "" }""")]
    [InlineData("""{ "Hotkey": "Nonsense+Q" }""")]
    [InlineData("""{ "Hotkey": ["Ctrl", "S"] }""")]
    public void V1MissingOrBrokenHotkey_BecomesPrintScreen(string json)
    {
        var (r, changed) = Migrate(json);
        Assert.True(changed);
        Assert.Equal(Entries(PsOn, WssOff), Hotkeys(r));
    }

    [Fact]
    public void V1HotkeyRejectedByRules_IsCarriedOverForNormalizeToDrop()
    {
        // “Shift+A” recognized but invalid: Normalize drops it and enables PrintScreen.
        var (r, _) = Migrate("""{ "Hotkey": "Shift+A" }""");
        var settings = r.Deserialize<AppSettings>()!.Normalize();
        Assert.Equal([HotkeyBinding.PrintScreen(true), HotkeyBinding.WinShiftS(false)], settings.Hotkeys);
    }

    [Fact]
    public void V2_IsUnchanged()
    {
        var json = """{ "SettingsVersion": 2, "Hotkeys": [ {"Kind":"PrintScreen","Enabled":false}, {"Kind":"Custom","Spec":"Ctrl+Q","Enabled":true} ] }""";
        var (r, changed) = Migrate(json);
        Assert.False(changed);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), r));
    }

    [Fact]
    public void MigratingTwice_SecondPassChangesNothing()
    {
        var (once, _) = Migrate("""{ "Hotkey": "Ctrl+Shift+X", "FallbackHotkey": "Ctrl+Alt+F5" }""");
        var (twice, changed) = SettingsMigrator.Migrate(once);
        Assert.False(changed);
        Assert.True(JsonNode.DeepEquals(once, twice));
    }

    [Fact]
    public void Migrate_DoesNotMutateInput()
    {
        var raw = JsonNode.Parse("""{ "Hotkey": "Ctrl+Shift+X" }""")!.AsObject();
        SettingsMigrator.Migrate(raw);
        Assert.Equal("""{"Hotkey":"Ctrl+Shift+X"}""", raw.ToJsonString(Readable));
    }

    [Fact]
    public void V2BrokenEntries_AreDropped()
    {
        var (r, changed) = Migrate("""
            { "SettingsVersion": 2, "Hotkeys": [
                {"Kind":"PrintScreen","Enabled":true},
                {"Kind":"Teleport","Enabled":true},
                {"Kind":"Custom","Spec":5,"Enabled":true},
                {"Kind":"Custom","Spec":"Ctrl+Q","Enabled":"yes"},
                {"Kind":"Custom","Spec":"Ctrl+Q","Enabled":null},
                {"Kind":null},
                {"Kind":"7"},
                "PrintScreen",
                {"Kind":"custom","Spec":"Ctrl+W","Enabled":true},
                {"Kind":1,"Enabled":true}
            ] }
            """);
        Assert.True(changed);
        Assert.Equal(Entries(PsOn, """{"Kind":"custom","Spec":"Ctrl+W","Enabled":true}""", """{"Kind":1,"Enabled":true}"""), Hotkeys(r));
        var settings = r.Deserialize<AppSettings>()!.Normalize(); // remainder deserializes
        Assert.Equal([HotkeyBinding.PrintScreen(true), HotkeyBinding.WinShiftS(true), HotkeyBinding.Custom("Ctrl+W")], settings.Hotkeys);
    }

    [Theory]
    [InlineData("""{ "SettingsVersion": 2, "Hotkeys": null }""")]
    [InlineData("""{ "SettingsVersion": 2, "Hotkeys": "PrintScreen" }""")]
    [InlineData("""{ "SettingsVersion": 2, "Hotkeys": {} }""")]
    public void V2HotkeysNotArray_IsRemoved(string json)
    {
        var (r, changed) = Migrate(json);
        Assert.True(changed);
        Assert.False(r.ContainsKey("Hotkeys"));
    }

    [Fact]
    public void V1WithHotkeysList_KeepsListAndDropsOldFields()
    {
        var (r, changed) = Migrate("""{ "Hotkey": "Ctrl+Q", "Hotkeys": [ {"Kind":"WinShiftS","Enabled":true} ] }""");
        Assert.True(changed);
        Assert.False(r.ContainsKey("Hotkey"));
        Assert.Equal("""[{"Kind":"WinShiftS","Enabled":true}]""", Hotkeys(r));
    }

    [Fact]
    public void FutureVersion_IsLeftAlone()
    {
        var json = """{ "SettingsVersion": 3, "Hotkey": "Ctrl+Q", "Hotkeys": 42 }""";
        var (r, changed) = Migrate(json);
        Assert.False(changed);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), r));
    }

    [Theory]
    [InlineData("""{ }""", 1)]
    [InlineData("""{ "SettingsVersion": 2 }""", 2)]
    [InlineData("""{ "SettingsVersion": "2" }""", 2)]
    [InlineData("""{ "SettingsVersion": "abc" }""", 1)]
    [InlineData("""{ "SettingsVersion": null }""", 1)]
    public void ReadVersion(string json, int expected) => Assert.Equal(expected, SettingsMigrator.ReadVersion(JsonNode.Parse(json)!.AsObject()));
}

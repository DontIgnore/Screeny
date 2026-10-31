using System.Text.Json;
using System.Text.Json.Nodes;
using Screeny.Hotkeys;

namespace Screeny.Settings;

/// <summary>
/// Converts “raw” settings JSON to current format (<see cref="AppSettings.CurrentVersion"/>) before deserialization.
/// Pure function: doesn't read/write files, doesn't modify input.
/// <list type="bullet">
/// <item>v1 (no <c>SettingsVersion</c>): <c>Hotkey</c> = PrintScreen (or missing/unrecognized) → PrintScreen entry
/// enabled; any other recognized combo → PrintScreen disabled + custom entry with combo.
/// <c>FallbackHotkey</c> dropped: fallback hotkey no longer configurable.</item>
/// <item>Any version ≤ current: from <c>Hotkeys</c> drop entries where deserialization would fail (not object,
/// unknown <c>Kind</c>, <c>Spec</c> not string, <c>Enabled</c> not true/false) — else one broken entry would reset whole file.</item>
/// <item>Any version ≤ current: from <c>DisabledFeatures</c> drop non-strings, field not array — removed.
/// Field appeared without version bump: old file lacks it, all features enabled.</item>
/// <item>Any version ≤ current: <c>Language</c> not string — removed (language “as system”), not reset whole file.
/// Field appeared without version bump: old file lacks it — also “as system”.</item>
/// <item>Any version ≤ current: <c>InstallId</c> not string — removed (created at next message send).
/// Field appeared without version bump.</item>
/// <item>Version newer than current (file from newer Screeny): untouched.</item>
/// </list>
/// </summary>
public static class SettingsMigrator
{
    public static (JsonObject Result, bool Changed) Migrate(JsonObject raw)
    {
        var obj = (JsonObject)raw.DeepClone();
        int version = ReadVersion(obj);
        if (version > AppSettings.CurrentVersion) return (obj, false);

        bool changed = false;
        if (version < 2) MigrateV1ToV2(obj);
        if (version < AppSettings.CurrentVersion)
        {
            obj["SettingsVersion"] = AppSettings.CurrentVersion;
            changed = true;
        }
        changed |= SanitizeHotkeys(obj);
        changed |= SanitizeStringList(obj, "DisabledFeatures");
        changed |= SanitizeString(obj, "Language");
        changed |= SanitizeString(obj, "InstallId", allowNull: true);
        return (obj, changed);
    }

    /// <summary>
    /// String field of other type (number, array, null) removed: default remains. With <paramref name="allowNull"/>
    /// null — valid value (field saved as null), not touched, so file not rewritten every launch.
    /// </summary>
    private static bool SanitizeString(JsonObject obj, string name, bool allowNull = false)
    {
        if (!obj.TryGetPropertyValue(name, out var node) || node?.GetValueKind() == JsonValueKind.String) return false;
        if (node is null && allowNull) return false;
        obj.Remove(name);
        return true;
    }

    /// <summary>String list: not array → field removed (default), non-strings dropped.</summary>
    /// <returns>true if something dropped.</returns>
    private static bool SanitizeStringList(JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var node)) return false;
        if (node is not JsonArray array)
        {
            obj.Remove(name);
            return true;
        }
        bool changed = false;
        for (int i = array.Count - 1; i >= 0; i--)
        {
            if (array[i]?.GetValueKind() == JsonValueKind.String) continue;
            array.RemoveAt(i);
            changed = true;
        }
        return changed;
    }

    /// <summary>Version from file; missing or not number → 1.</summary>
    public static int ReadVersion(JsonObject obj)
        => obj["SettingsVersion"] is JsonValue v && v.TryGetValue(out int n) ? n
         : obj["SettingsVersion"] is JsonValue s && s.TryGetValue(out string? text) && int.TryParse(text, out int parsed) ? parsed
         : 1;

    private static void MigrateV1ToV2(JsonObject obj)
    {
        string? hotkey = obj["Hotkey"] is JsonValue v && v.TryGetValue(out string? text) ? text : null;
        obj.Remove("Hotkey");
        obj.Remove("FallbackHotkey");
        if (obj.ContainsKey("Hotkeys")) return; // already a list (hand-assembled file) — don't overwrite

        var hotkeys = new JsonArray();
        bool custom = hotkey is not null && HotkeyParser.TryParse(hotkey, out var spec) && spec != HotkeySpec.PrintScreen;
        hotkeys.Add(Entry(HotkeyKind.PrintScreen, enabled: !custom));
        hotkeys.Add(Entry(HotkeyKind.WinShiftS, enabled: false));
        // Invalid by HotkeyRules combo (e.g., “Shift+A”) carried as-is: Normalize drops it with log
        // and, since no other hotkeys, enables PrintScreen.
        if (custom) hotkeys.Add(Entry(HotkeyKind.Custom, enabled: true, hotkey!.Trim()));
        obj["Hotkeys"] = hotkeys;
    }

    private static JsonObject Entry(HotkeyKind kind, bool enabled, string? spec = null)
    {
        var e = new JsonObject { ["Kind"] = kind.ToString() };
        if (spec is not null) e["Spec"] = spec;
        e["Enabled"] = enabled;
        return e;
    }

    /// <returns>true if something dropped.</returns>
    private static bool SanitizeHotkeys(JsonObject obj)
    {
        if (!obj.TryGetPropertyValue("Hotkeys", out var node)) return false;
        if (node is not JsonArray array)
        {
            obj.Remove("Hotkeys"); // null or not array → defaults
            return true;
        }
        bool changed = false;
        for (int i = array.Count - 1; i >= 0; i--)
        {
            if (IsWellFormed(array[i])) continue;
            array.RemoveAt(i);
            changed = true;
        }
        return changed;
    }

    private static bool IsWellFormed(JsonNode? item)
    {
        if (item is not JsonObject e) return false;
        if (e["Kind"] is not JsonValue kind || !IsKnownKind(kind)) return false;
        if (e.TryGetPropertyValue("Spec", out var spec) && spec is not null && spec.GetValueKind() != JsonValueKind.String) return false;
        // null in bool doesn't deserialize — such entry broken too.
        if (e.TryGetPropertyValue("Enabled", out var enabled)
            && enabled?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False)) return false;
        return true;
    }

    private static bool IsKnownKind(JsonValue kind)
    {
        if (kind.TryGetValue(out string? name))
            return !name.Any(char.IsAsciiDigit) && Enum.TryParse<HotkeyKind>(name, ignoreCase: true, out var k) && Enum.IsDefined(k);
        return kind.TryGetValue(out int n) && Enum.IsDefined((HotkeyKind)n);
    }
}

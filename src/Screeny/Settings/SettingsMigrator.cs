using System.Text.Json;
using System.Text.Json.Nodes;
using Screeny.Hotkeys;

namespace Screeny.Settings;

/// <summary>
/// Приводит «сырой» JSON настроек к текущему формату (<see cref="AppSettings.CurrentVersion"/>) до десериализации.
/// Чистая функция: не читает и не пишет файлы, вход не изменяет.
/// <list type="bullet">
/// <item>v1 (нет <c>SettingsVersion</c>): <c>Hotkey</c> = PrintScreen (или поле отсутствует/нераспознано) → запись
/// PrintScreen включена; любое другое распознанное сочетание → PrintScreen выключен + своя запись с этим сочетанием.
/// <c>FallbackHotkey</c> отбрасывается: запасной хоткей теперь не настраивается.</item>
/// <item>Любая версия ≤ текущей: из <c>Hotkeys</c> выбрасываются записи, на которых упала бы десериализация (не объект,
/// неизвестный <c>Kind</c>, <c>Spec</c> не строка, <c>Enabled</c> не true/false), — иначе одна битая запись сбросила бы весь файл.</item>
/// <item>Любая версия ≤ текущей: из <c>DisabledFeatures</c> выбрасываются не строки, поле не массив — убирается.
/// Само поле появилось без смены версии: в старом файле его нет, и все функции включены.</item>
/// <item>Любая версия ≤ текущей: <c>Language</c> не строка — убирается (язык «как в системе»), а не сбрасывает весь файл.
/// Поле появилось без смены версии: у старого файла его нет — тоже «как в системе».</item>
/// <item>Версия новее текущей (файл от более новой Screeny): не трогаем.</item>
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
        return (obj, changed);
    }

    /// <summary>Строковое поле другого типа (число, массив, null) убирается: остаётся значение по умолчанию.</summary>
    private static bool SanitizeString(JsonObject obj, string name)
    {
        if (!obj.TryGetPropertyValue(name, out var node) || node?.GetValueKind() == JsonValueKind.String) return false;
        obj.Remove(name);
        return true;
    }

    /// <summary>Список строк: не массив → поле убирается (значение по умолчанию), не строки выбрасываются.</summary>
    /// <returns>true, если что-то выброшено.</returns>
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

    /// <summary>Версия из файла; поле отсутствует или не число → 1.</summary>
    public static int ReadVersion(JsonObject obj)
        => obj["SettingsVersion"] is JsonValue v && v.TryGetValue(out int n) ? n
         : obj["SettingsVersion"] is JsonValue s && s.TryGetValue(out string? text) && int.TryParse(text, out int parsed) ? parsed
         : 1;

    private static void MigrateV1ToV2(JsonObject obj)
    {
        string? hotkey = obj["Hotkey"] is JsonValue v && v.TryGetValue(out string? text) ? text : null;
        obj.Remove("Hotkey");
        obj.Remove("FallbackHotkey");
        if (obj.ContainsKey("Hotkeys")) return; // уже есть список (файл, собранный вручную) — не перетираем

        var hotkeys = new JsonArray();
        bool custom = hotkey is not null && HotkeyParser.TryParse(hotkey, out var spec) && spec != HotkeySpec.PrintScreen;
        hotkeys.Add(Entry(HotkeyKind.PrintScreen, enabled: !custom));
        hotkeys.Add(Entry(HotkeyKind.WinShiftS, enabled: false));
        // Недопустимое по HotkeyRules сочетание (например, «Shift+A») переносим как есть: Normalize выбросит его с записью
        // в журнал и, раз других хоткеев нет, включит PrintScreen.
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

    /// <returns>true, если что-то выброшено.</returns>
    private static bool SanitizeHotkeys(JsonObject obj)
    {
        if (!obj.TryGetPropertyValue("Hotkeys", out var node)) return false;
        if (node is not JsonArray array)
        {
            obj.Remove("Hotkeys"); // null или не массив → значения по умолчанию
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
        // null в bool не десериализуется — такая запись тоже битая.
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

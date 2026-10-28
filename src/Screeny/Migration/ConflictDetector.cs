using System.Text.Json;
using Screeny.Hotkeys;

namespace Screeny.Migration;

/// <summary>
/// Найденная мешающая программа.
/// </summary>
/// <param name="Processes">Запущенные процессы программы (их закрывает «Закрыть сейчас»).</param>
/// <param name="Autostarts">Включённые записи автозапуска (выключенные через StartupApproved сюда не попадают).</param>
/// <param name="Uninstaller">Запись «Программы и компоненты» с родным деинсталлятором или null (портативная версия).</param>
/// <param name="Keys">Хоткеи Screeny, которые она обычно занимает (у PowerToys — по переназначениям Keyboard Manager).</param>
public sealed record Conflict(
    ConflictProgram Program,
    IReadOnlyList<ProcessEntry> Processes,
    IReadOnlyList<AutostartEntry> Autostarts,
    UninstallEntry? Uninstaller,
    IReadOnlySet<HotkeyKind> Keys)
{
    public string Id => Program.Id;
    public bool IsRunning => Processes.Count > 0;
    public IEnumerable<AutostartEntry> UserAutostarts => Autostarts.Where(a => !a.IsMachineWide);
    public IEnumerable<AutostartEntry> MachineAutostarts => Autostarts.Where(a => a.IsMachineWide);

    /// <summary>Хотя бы одна запись автозапуска — для всех пользователей: отключить её можно только с правами администратора.</summary>
    public bool NeedsAdminForAutostart => Autostarts.Any(a => a.IsMachineWide);

    /// <summary>Путь exe для иконки: запущенный процесс или команда автозапуска.</summary>
    public string? ExePath => Processes.Select(p => p.Path).FirstOrDefault(p => p is not null)
                              ?? Autostarts.Select(a => UninstallCommand.ExtractExePath(a.Command)).FirstOrDefault(p => p is not null);

    /// <summary>Мешает ли выбранным хоткеям (пересечение с включёнными PrintScreen / Win+Shift+S).</summary>
    public bool Blocks(IEnumerable<HotkeyBinding> hotkeys) => hotkeys.Any(h => h.Enabled && Keys.Contains(h.Kind));
}

/// <summary>Поиск мешающих программ по снимку среды. Чистая функция: ничего не читает сама и ничего не меняет.</summary>
public static class ConflictDetector
{
    public static IReadOnlyList<Conflict> Detect(IEnvironmentSnapshot env, ConflictCatalog catalog)
    {
        var result = new List<Conflict>();
        foreach (var program in catalog.Programs)
        {
            var uninstaller = env.Uninstall
                .Where(u => program.MatchesUninstall(u.DisplayName) && UninstallCommand.Parse(u.UninstallString) is not null)
                // У PowerToys две записи (MSI и пакет установщика): пакет .exe /uninstall удаляет всё вместе.
                .OrderBy(u => u.UninstallString!.Contains("msiexec", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
                .FirstOrDefault();
            if (program.DetectByRemapOnly)
            {
                var remapped = PowerToysRemaps.BlockedKeys(env.PowerToysKeyboardConfig);
                if (remapped.Count == 0) continue;
                var ptProcesses = env.Processes.Where(p => program.MatchesProcess(p.Name)).ToList();
                result.Add(new Conflict(program, ptProcesses, [], uninstaller, remapped));
                continue;
            }

            var processes = env.Processes.Where(p => program.MatchesProcess(p.Name)).OrderBy(p => p.Pid).ToList();
            var autostarts = env.Autostart.Where(a => a.IsEnabled && program.MatchesAutostart(a)).ToList();
            if (processes.Count == 0 && autostarts.Count == 0) continue; // установлена, но не запущена и не стартует сама
            result.Add(new Conflict(program, processes, autostarts, uninstaller, program.TypicalKeys));
        }
        return result;
    }

    /// <summary>PrintScreen открывает системный захват экрана Windows (значение 1 или нет значения на Windows 11).</summary>
    public static bool IsSystemPrintScreenOn(IEnvironmentSnapshot env)
        => PrintScreenPolicy.IsSnippingKeyEnabled(env.PrintScreenKeyForSnippingEnabled, env.OsBuild);
}

/// <summary>
/// Переназначения Keyboard Manager из PowerToys, которые задевают хоткеи Screeny. Формат файла конфигурации:
/// <c>remapKeys.inProcess[].originalKeys</c> — код VK одной клавиши десятичным числом («44» — PrintScreen),
/// <c>remapShortcuts.global[].originalKeys</c> — коды через «;» («91;16;83» — Win+Shift+S).
/// </summary>
public static class PowerToysRemaps
{
    private const int VkSnapshot = 44, VkS = 83;
    private static readonly int[] WinKeys = [91, 92];
    private static readonly int[] ShiftKeys = [16, 160, 161];

    public static IReadOnlySet<HotkeyKind> BlockedKeys(string? json)
    {
        var keys = new HashSet<HotkeyKind>();
        if (string.IsNullOrWhiteSpace(json)) return keys;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            foreach (var item in Items(root, "remapKeys", "inProcess"))
                if (Codes(item).SequenceEqual([VkSnapshot])) keys.Add(HotkeyKind.PrintScreen);
            foreach (var group in new[] { "global", "appSpecific" })
                foreach (var item in Items(root, "remapShortcuts", group))
                {
                    var codes = Codes(item);
                    if (codes.Count == 3 && codes.Contains(VkS) && codes.Any(WinKeys.Contains) && codes.Any(ShiftKeys.Contains))
                        keys.Add(HotkeyKind.WinShiftS);
                    if (codes.Count == 1 && codes[0] == VkSnapshot) keys.Add(HotkeyKind.PrintScreen);
                }
        }
        catch (JsonException) { /* повреждённый файл — считаем, что переназначений нет */ }
        return keys;
    }

    private static IEnumerable<JsonElement> Items(JsonElement root, string section, string list)
    {
        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(section, out var s) && s.ValueKind == JsonValueKind.Object
            && s.TryGetProperty(list, out var l) && l.ValueKind == JsonValueKind.Array)
            return l.EnumerateArray();
        return [];
    }

    private static List<int> Codes(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("originalKeys", out var keys) || keys.ValueKind != JsonValueKind.String)
            return [];
        var codes = new List<int>();
        foreach (var part in (keys.GetString() ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (int.TryParse(part, out int code)) codes.Add(code);
        return codes;
    }
}

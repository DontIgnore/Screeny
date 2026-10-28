using Screeny.Hotkeys;

namespace Screeny.Migration;

/// <summary>
/// Программа, которая может забирать хоткеи Screeny.
/// </summary>
/// <param name="Id">Короткий id (белый список для <c>--elevated-action</c>): только латиница и цифры.</param>
/// <param name="ExeNames">Имена exe: по ним ищутся процессы (без «.exe»), записи Run (по команде) и ярлыки автозагрузки (по цели).</param>
/// <param name="AutostartNames">Известные имена значения Run или файла ярлыка («ShareX.lnk») — второй признак, если exe в команде другой.</param>
/// <param name="UninstallNames">Начало DisplayName в «Программы и компоненты» (дальше — конец строки, пробел, «-» или «(»).</param>
/// <param name="TypicalKeys">Какие хоткеи Screeny программа занимает в настройках по умолчанию.</param>
/// <param name="KeysText">Её клавиши по умолчанию — для подписи в мастере (у PowerToys подпись своя, из строк интерфейса).</param>
/// <param name="DetectByRemapOnly">PowerToys: мешает только переназначение клавиш в Keyboard Manager, а не сам запуск.</param>
public sealed record ConflictProgram(
    string Id,
    string DisplayName,
    IReadOnlyList<string> ExeNames,
    IReadOnlyList<string> AutostartNames,
    IReadOnlyList<string> UninstallNames,
    IReadOnlySet<HotkeyKind> TypicalKeys,
    string KeysText,
    bool DetectByRemapOnly = false)
{
    public bool MatchesExe(string? fileName)
        => fileName is not null && ExeNames.Any(e => string.Equals(e, fileName, StringComparison.OrdinalIgnoreCase));

    public bool MatchesProcess(string processName)
        => ExeNames.Any(e => string.Equals(Path.GetFileNameWithoutExtension(e), processName, StringComparison.OrdinalIgnoreCase));

    public bool MatchesAutostart(AutostartEntry entry)
        => MatchesExe(entry.ExeFileName) || AutostartNames.Any(n => string.Equals(n, entry.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>«Lightshot-5.5.0.7», «Greenshot 1.2.10.6», «PowerToys (Preview) x64» — да; «Greenshotter» — нет.</summary>
    public bool MatchesUninstall(string displayName)
    {
        foreach (var name in UninstallNames)
        {
            if (!displayName.StartsWith(name, StringComparison.OrdinalIgnoreCase)) continue;
            if (displayName.Length == name.Length) return true;
            char next = displayName[name.Length];
            if (next is ' ' or '-' or '(' or '_' or '.') return true;
        }
        return false;
    }
}

/// <summary>
/// Таблица программ, которые мешают хоткеям Screeny. Признаки сверены с исходниками программ и с установленным
/// Lightshot (подробности — в комментариях у каждой записи).
/// </summary>
public sealed class ConflictCatalog(IReadOnlyList<ConflictProgram> programs)
{
    private static readonly IReadOnlySet<HotkeyKind> PrintScreenOnly = new HashSet<HotkeyKind> { HotkeyKind.PrintScreen };
    private static readonly IReadOnlySet<HotkeyKind> None = new HashSet<HotkeyKind>();

    public IReadOnlyList<ConflictProgram> Programs { get; } = programs;

    public ConflictProgram? Find(string id) => Programs.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.Ordinal));

    public static ConflictCatalog Default { get; } = new(
    [
        // Типичная установка: HKLM WOW6432Node Run, значение «Lightshot» → «…\Skillbrains\lightshot\Lightshot.exe»;
        // запущенный процесс — «…\lightshot\5.5.0.7\Lightshot.exe»; Uninstall: «Lightshot-5.5.0.7» (Inno Setup, unins000.exe).
        new("lightshot", "Lightshot", ["Lightshot.exe"], ["Lightshot"], ["Lightshot"], PrintScreenOnly, "PrintScreen"),
        // ShareX/StartupManager.cs: ярлык «ShareX.lnk» в папке «Автозагрузка» с ключом -silent, флаг — HKCU StartupApproved\StartupFolder.
        // По умолчанию: PrintScreen — весь экран, Ctrl+PrintScreen — область, Alt+PrintScreen — окно.
        new("sharex", "ShareX", ["ShareX.exe"], ["ShareX.lnk", "ShareX"], ["ShareX"], PrintScreenOnly, "PrintScreen, Ctrl+PrintScreen, Alt+PrintScreen"),
        // Greenshot/Helpers/StartupHelper.cs: значение Run «Greenshot» (HKCU, HKLM и WOW6432Node) или ярлык «Greenshot.lnk».
        new("greenshot", "Greenshot", ["Greenshot.exe"], ["Greenshot", "Greenshot.lnk"], ["Greenshot"], PrintScreenOnly, "PrintScreen, Alt+PrintScreen, Ctrl+PrintScreen"),
        // Snipaste: процесс Snipaste.exe; F1 — снимок, F3 — закрепить. С хоткеями Screeny по умолчанию не пересекается,
        // но это второй инструмент снимков. Имя записи автозапуска не подтверждено — ищем по exe в команде.
        new("snipaste", "Snipaste", ["Snipaste.exe"], ["Snipaste"], ["Snipaste"], None, "F1, F3"),
        // PicPick (NGWIN): PrintScreen — весь экран, Alt/Ctrl/Shift+PrintScreen — окно и область. Имя записи автозапуска
        // не подтверждено — ищем по exe в команде или цели ярлыка.
        new("picpick", "PicPick", ["picpick.exe"], ["PicPick", "PicPick.lnk"], ["PicPick"], PrintScreenOnly, "PrintScreen, Alt+PrintScreen, Shift+PrintScreen"),
        // PowerToys: PrintScreen и Win+Shift+S по умолчанию не занимает; мешает только переназначение этих клавиш
        // в Keyboard Manager. Автозапуск — задача планировщика, его отключают в настройках самого PowerToys.
        new("powertoys", "PowerToys", ["PowerToys.exe"], [], ["PowerToys"], None, "Keyboard Manager", DetectByRemapOnly: true),
    ]);
}

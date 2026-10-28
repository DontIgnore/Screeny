using Screeny.Hotkeys;
using Screeny.Settings;

namespace Screeny.Migration;

/// <summary>Что выбрано в секции хоткеев мастера.</summary>
public sealed record WizardHotkeyChoice(bool PrintScreen, bool WinShiftS, HotkeySpec? Custom);

/// <summary>Правила мастера первого запуска: когда показывать, что считать новым конфликтом, какой список хоткеев применить.</summary>
public static class SetupWizard
{
    /// <summary>
    /// Текущая версия мастера. Поднять, если мастер нужно показать заново всем, кто его уже прошёл.
    /// 2 — в мастере появился выбор функций: установленные копии видят его один раз.
    /// </summary>
    public const int Version = 2;

    /// <summary>Показать мастер при запуске: пользователь ещё не проходил (или не пропускал) эту версию.</summary>
    public static bool ShouldShowOnStartup(AppSettings settings) => settings.SetupWizardVersion < Version;

    /// <summary>
    /// Конфликты, о которых стоит напомнить уведомлением после пройденного мастера: программа мешает включённому хоткею
    /// и её не было среди уже известных (показанных в мастере или в прошлом уведомлении).
    /// </summary>
    public static IReadOnlyList<Conflict> NewBlockingConflicts(IReadOnlyList<Conflict> conflicts, IEnumerable<HotkeyBinding> hotkeys,
        IEnumerable<string> known)
    {
        var knownSet = new HashSet<string>(known, StringComparer.Ordinal);
        var list = hotkeys.ToList();
        return conflicts.Where(c => !knownSet.Contains(c.Id) && c.Blocks(list)).ToList();
    }

    /// <summary>Что сделать при запуске.</summary>
    public enum StartupAction { Nothing, ShowWizard, CheckForNewConflicts }

    /// <summary>
    /// Мастер — при явном <c>--setup-wizard</c> или если рабочий экземпляр его ещё не проходил; иначе рабочий экземпляр
    /// в фоне проверяет новые конфликты. Тестовый экземпляр без ключа ничего не показывает и реальную среду не проверяет.
    /// </summary>
    public static StartupAction OnStartup(bool isDev, bool wizardRequested, AppSettings settings)
        => wizardRequested ? StartupAction.ShowWizard
            : isDev ? StartupAction.Nothing
            : ShouldShowOnStartup(settings) ? StartupAction.ShowWizard
            : StartupAction.CheckForNewConflicts;

    /// <summary>
    /// Проверка при запуске: известными остаются только программы, которые всё ещё найдены (исправленная и потом вернувшаяся
    /// снова считается новой); новые, мешающие включённому хоткею, возвращаются для уведомления и сразу становятся известными.
    /// </summary>
    public static (IReadOnlyList<Conflict> Fresh, List<string> Known) CheckForNewConflicts(IReadOnlyList<Conflict> conflicts,
        IEnumerable<HotkeyBinding> hotkeys, IEnumerable<string> known)
    {
        var present = conflicts.Select(c => c.Id).ToHashSet(StringComparer.Ordinal);
        var stillKnown = known.Where(present.Contains).Distinct(StringComparer.Ordinal).ToList();
        var fresh = NewBlockingConflicts(conflicts, hotkeys, stillKnown);
        return (fresh, [.. stillKnown, .. fresh.Select(c => c.Id)]);
    }

    /// <summary>Текст уведомления в трее: «PrintScreen занят программой Lightshot — открыть мастер?».</summary>
    public static string NotificationText(IReadOnlyList<Conflict> conflicts, IEnumerable<HotkeyBinding> hotkeys)
    {
        var list = hotkeys.ToList();
        var first = conflicts[0];
        var key = list.Where(h => h.Enabled && first.Keys.Contains(h.Kind)).Select(h => h.DisplayName).FirstOrDefault() ?? Localization.Loc.T("Wizard.HotkeyGeneric");
        var names = string.Join(Localization.Loc.ListSeparator, conflicts.Select(c => c.Program.DisplayName));
        return Localization.Loc.F(conflicts.Count == 1 ? "Wizard.NotifyOne" : "Wizard.NotifyMany", key, names);
    }

    /// <summary>
    /// Список хоткеев после мастера: PrintScreen и Win+Shift+S — по галочкам, своё сочетание из поля добавляется (включённым)
    /// к уже настроенным; прочие свои сочетания не трогаются. Нормализация — в <see cref="AppSettings.NormalizeHotkeys"/>.
    /// </summary>
    public static List<HotkeyBinding> BuildHotkeys(IEnumerable<HotkeyBinding> current, WizardHotkeyChoice choice)
    {
        var customs = current.Where(b => b.Kind == HotkeyKind.Custom).ToList();
        if (choice.Custom is { } spec)
        {
            customs.RemoveAll(b => b.ToRegisterSpec() == spec);
            customs.Add(HotkeyBinding.Custom(spec.ToString(), enabled: true));
        }
        return AppSettings.NormalizeHotkeys([HotkeyBinding.PrintScreen(choice.PrintScreen), HotkeyBinding.WinShiftS(choice.WinShiftS), .. customs]);
    }

    /// <summary>Хотя бы один хоткей включён (иначе снимок — только из трея).</summary>
    public static bool HasAnyHotkey(WizardHotkeyChoice choice, IEnumerable<HotkeyBinding> current)
        => choice.PrintScreen || choice.WinShiftS || choice.Custom is not null || current.Any(b => b.Kind == HotkeyKind.Custom && b.Enabled);
}

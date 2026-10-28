namespace Screeny.Hotkeys;

/// <summary>Состояние одной записи хоткея после <see cref="HotkeyManager.Apply"/>.</summary>
public enum HotkeyStatus
{
    /// <summary>Работает.</summary>
    Active,
    /// <summary>Запись выключена в настройках.</summary>
    Disabled,
    /// <summary>Сочетание уже занято другим приложением (ERROR_HOTKEY_ALREADY_REGISTERED, 1409).</summary>
    Busy,
    /// <summary>Регистрация или установка хука не удалась по другой причине (код в <see cref="HotkeyState.Win32Error"/>).</summary>
    Failed,
    /// <summary>PrintScreen забирает системный захват экрана Windows: WM_HOTKEY не придёт, регистрацию не пробуем.</summary>
    ReservedBySystem,
    /// <summary>Своё сочетание не распознано.</summary>
    Invalid,
    /// <summary>Не регистрируется в этом экземпляре (<c>--dev</c> без <c>--enable-ll-hook</c>).</summary>
    NotAllowed,
}

/// <remarks>Хранит вид проблемы и код ошибки, а текст <see cref="Message"/> собирает при каждом чтении — на текущем
/// языке интерфейса, поэтому после смены языка подсказки в настройках и уведомления не устаревают.</remarks>
public sealed record HotkeyState(HotkeyBinding Binding, HotkeyStatus Status, int Win32Error = 0)
{
    public bool IsActive => Status == HotkeyStatus.Active;

    /// <summary>Текст проблемы для пользователя; null — проблемы нет.</summary>
    public string? Message => Status switch
    {
        HotkeyStatus.ReservedBySystem => Localization.Loc.T("Hotkeys.PrintScreenReserved"),
        HotkeyStatus.Invalid => Localization.Loc.F("Hotkeys.Unrecognized", Binding.Spec),
        HotkeyStatus.Busy or HotkeyStatus.Failed when Binding.Kind == HotkeyKind.WinShiftS => Localization.Loc.F("Hotkeys.HookFailed", Win32Error),
        HotkeyStatus.Busy or HotkeyStatus.Failed when Binding.ToRegisterSpec() is { } spec => GlobalHotkey.DescribeFailure(spec, Win32Error),
        _ => null,
    };
}

/// <summary>Итог применения списка хоткеев: состояние каждой записи и запасной хоткей, если он включён.</summary>
/// <param name="FallbackErrorCode">Код ошибки Windows, если запасной хоткей не зарегистрировался.</param>
public sealed record HotkeyReport(IReadOnlyList<HotkeyState> Entries, HotkeySpec? Fallback = null, int? FallbackErrorCode = null)
{
    /// <summary>Почему не включился запасной хоткей — текстом на текущем языке; null — не было ошибки.</summary>
    public string? FallbackError => FallbackErrorCode is { } code ? GlobalHotkey.DescribeFailure(HotkeySpec.Fallback, code) : null;

    public static readonly HotkeyReport Empty = new([]);

    /// <summary>Подписи всех работающих хоткеев в порядке списка, запасной — последним.</summary>
    public IReadOnlyList<string> ActiveNames =>
        [.. Entries.Where(e => e.IsActive).Select(e => e.Binding.DisplayName), .. Fallback is { } f ? [f.ToString()] : Array.Empty<string>()];

    public HotkeyState? Find(HotkeyKind kind) => Entries.FirstOrDefault(e => e.Binding.Kind == kind);

    /// <summary>Все работающие хоткеи через « / » для меню и подсказки трея; «—», если нет ни одного.</summary>
    public string ActiveText(int maxLength = 32) => FormatNames(ActiveNames, maxLength);

    /// <summary>Имена через « / »; если длиннее <paramref name="maxLength"/> — первое и « / …».</summary>
    public static string FormatNames(IReadOnlyList<string> names, int maxLength = 32)
    {
        if (names.Count == 0) return "—";
        var all = string.Join(" / ", names);
        return all.Length <= maxLength || names.Count == 1 ? all : names[0] + " / …";
    }

    /// <summary>
    /// Текст уведомления о проблемах или null, если сообщать не о чем. Недоступный PrintScreen упоминается, только
    /// когда из-за него включился запасной хоткей или не работает ничего: если работают другие сочетания, а PrintScreen
    /// забирает Windows, пользователь видит это в настройках и не получает уведомление при каждом запуске.
    /// </summary>
    public string? ProblemSummary()
    {
        var reasons = new List<string>();
        bool anyActive = ActiveNames.Count > 0;
        foreach (var e in Entries)
        {
            if (e.Message is null || e.Status is HotkeyStatus.Active or HotkeyStatus.Disabled or HotkeyStatus.NotAllowed) continue;
            if (e.Binding.Kind == HotkeyKind.PrintScreen && Fallback is null && anyActive) continue;
            reasons.Add(e.Message);
        }
        if (reasons.Count == 0) return null;
        var text = string.Join(" ", reasons);
        if (Fallback is { } fb) return Localization.Loc.F("Hotkeys.FallbackInUse", text, fb);
        if (!anyActive) return FallbackError is null ? Localization.Loc.F("Hotkeys.TrayOnly", text) : Localization.Loc.F("Hotkeys.TrayOnlyWithError", text, FallbackError);
        return text;
    }
}

/// <summary>Результат <see cref="HotkeyManager.Apply"/>: при неудаче <see cref="Error"/> — запись, из-за которой всё откатили.</summary>
public sealed record HotkeyApplyResult(bool Success, HotkeyReport Report, HotkeyState? Error = null);

/// <summary>Системные операции, которые нужны <see cref="HotkeyManager"/>; в тестах подменяются, чтобы не трогать реальные хоткеи.</summary>
public interface IHotkeyBackend
{
    /// <summary>RegisterHotKey; <paramref name="pressed"/> вызывается в UI-потоке.</summary>
    bool TryRegister(HotkeySpec spec, Action pressed, out IDisposable? handle, out int win32Error);

    /// <summary>Низкоуровневый хук Win+Shift+S; <paramref name="triggered"/> вызывается в UI-потоке.</summary>
    bool TryInstallWinShiftSHook(Action triggered, out IDisposable? hook, out int win32Error);

    /// <summary>Включён ли в Windows «PrintScreen открывает захват экрана».</summary>
    bool IsPrintScreenReservedBySystem();
}

/// <summary>
/// Регистрирует все включённые хоткеи снимка: PrintScreen и свои сочетания — <c>RegisterHotKey</c> (каждый своим id),
/// Win+Shift+S — низкоуровневым хуком. Вызывать из UI-потока.
/// <para>
/// Запасной <see cref="HotkeySpec.Fallback"/> (<c>Ctrl+Alt+S</c>) регистрируется автоматически, если PrintScreen включён,
/// но недоступен (системный захват Windows или ошибка регистрации), а ни один другой хоткей не работает.
/// </para>
/// <para>
/// <see cref="Apply"/> атомарен в строгом режиме: уже зарегистрированные сочетания переиспользуются (повторная
/// регистрация своего же сочетания дала бы 1409), новые регистрируются, и только если ни одна <em>вновь включённая</em>
/// запись не провалилась, снимаются лишние. Иначе новые снимаются, прежнее состояние остаётся как было.
/// </para>
/// </summary>
public sealed class HotkeyManager(IHotkeyBackend backend, bool allowRegisterHotKey, bool allowHook) : IDisposable
{
    private readonly Dictionary<HotkeySpec, IDisposable> _registered = new();
    private IDisposable? _hook;
    private IReadOnlyList<HotkeyBinding> _applied = [];

    /// <summary>Нажат любой из хоткеев (в UI-потоке).</summary>
    public event Action? Pressed;

    /// <summary>Отчёт последнего успешного <see cref="Apply"/>.</summary>
    public HotkeyReport Report { get; private set; } = HotkeyReport.Empty;

    /// <summary>Применённый список (последний успешный <see cref="Apply"/>).</summary>
    public IReadOnlyList<HotkeyBinding> Applied => _applied;

    public bool IsHookInstalled => _hook is not null;

    /// <param name="strict">
    /// true (сохранение настроек, мастер): если вновь включённая запись не заработала (занята, ошибка, хук не поставился),
    /// ничего не меняется и возвращается ошибка. Недоступный из-за Windows PrintScreen ошибкой не считается — работает
    /// запасной хоткей. false (запуск): регистрируется всё, что получилось, проблемы — в отчёте.
    /// </param>
    public HotkeyApplyResult Apply(IReadOnlyList<HotkeyBinding> bindings, bool strict)
    {
        bool printScreenReserved = allowRegisterHotKey && bindings.Any(b => b.Kind == HotkeyKind.PrintScreen && b.Enabled)
                                   && backend.IsPrintScreenReservedBySystem();
        var added = new Dictionary<HotkeySpec, IDisposable>();
        var keep = new HashSet<HotkeySpec>();
        IDisposable? newHook = null;
        bool keepHook = false;
        var states = new List<HotkeyState>();

        foreach (var b in bindings)
        {
            if (!b.Enabled) { states.Add(new HotkeyState(b, HotkeyStatus.Disabled)); continue; }
            if (b.Kind == HotkeyKind.WinShiftS) { states.Add(ApplyHook(b, ref newHook, ref keepHook)); continue; }
            if (!allowRegisterHotKey) { states.Add(new HotkeyState(b, HotkeyStatus.NotAllowed)); continue; }
            if (b.Kind == HotkeyKind.PrintScreen && printScreenReserved)
            {
                states.Add(new HotkeyState(b, HotkeyStatus.ReservedBySystem));
                continue;
            }
            if (b.ToRegisterSpec() is not { } spec) { states.Add(new HotkeyState(b, HotkeyStatus.Invalid)); continue; }
            states.Add(Register(b, spec, added, keep));
        }

        if (strict && FirstNewFailure(states) is { } failure)
        {
            foreach (var h in added.Values) h.Dispose();
            newHook?.Dispose();
            return new HotkeyApplyResult(false, Report, failure);
        }

        HotkeySpec? fallback = null;
        int? fallbackError = null;
        if (NeedsFallback(states))
        {
            if (keep.Contains(HotkeySpec.Fallback) || added.ContainsKey(HotkeySpec.Fallback)) fallback = HotkeySpec.Fallback;
            else if (_registered.ContainsKey(HotkeySpec.Fallback)) { keep.Add(HotkeySpec.Fallback); fallback = HotkeySpec.Fallback; }
            else if (backend.TryRegister(HotkeySpec.Fallback, OnPressed, out var h, out int error) && h is not null)
            { added[HotkeySpec.Fallback] = h; fallback = HotkeySpec.Fallback; }
            else fallbackError = error;
        }

        foreach (var (spec, handle) in _registered.ToList())
        {
            if (keep.Contains(spec)) continue;
            handle.Dispose();
            _registered.Remove(spec);
        }
        foreach (var (spec, handle) in added) _registered[spec] = handle;
        if (!keepHook) { _hook?.Dispose(); _hook = null; }
        if (newHook is not null) _hook = newHook;

        _applied = [.. bindings];
        Report = new HotkeyReport(states, fallback, fallbackError);
        return new HotkeyApplyResult(true, Report);
    }

    private HotkeyState Register(HotkeyBinding b, HotkeySpec spec, Dictionary<HotkeySpec, IDisposable> added, HashSet<HotkeySpec> keep)
    {
        if (_registered.ContainsKey(spec) || added.ContainsKey(spec)) { keep.Add(spec); return new HotkeyState(b, HotkeyStatus.Active); }
        if (backend.TryRegister(spec, OnPressed, out var handle, out int error) && handle is not null)
        {
            added[spec] = handle;
            return new HotkeyState(b, HotkeyStatus.Active);
        }
        return new HotkeyState(b, GlobalHotkey.IsAlreadyRegistered(error) ? HotkeyStatus.Busy : HotkeyStatus.Failed, error);
    }

    private HotkeyState ApplyHook(HotkeyBinding b, ref IDisposable? newHook, ref bool keepHook)
    {
        if (!allowHook) return new HotkeyState(b, HotkeyStatus.NotAllowed);
        if (_hook is not null || newHook is not null) { keepHook = _hook is not null; return new HotkeyState(b, HotkeyStatus.Active); }
        if (backend.TryInstallWinShiftSHook(OnPressed, out var hook, out int error) && hook is not null)
        {
            newHook = hook;
            return new HotkeyState(b, HotkeyStatus.Active);
        }
        return new HotkeyState(b, HotkeyStatus.Failed, error);
    }

    /// <summary>Первая вновь включённая запись, которая не заработала (Busy, Failed, Invalid).</summary>
    private HotkeyState? FirstNewFailure(List<HotkeyState> states)
        => states.FirstOrDefault(s => (s.Status is HotkeyStatus.Busy or HotkeyStatus.Failed or HotkeyStatus.Invalid)
                                      && !_applied.Any(a => a.Enabled && a.Kind == s.Binding.Kind && SameSpec(a, s.Binding)));

    private static bool SameSpec(HotkeyBinding a, HotkeyBinding b)
        => a.Kind != HotkeyKind.Custom || a.ToRegisterSpec() == b.ToRegisterSpec();

    private bool NeedsFallback(List<HotkeyState> states)
        => allowRegisterHotKey
           && states.Any(s => s.Binding.Kind == HotkeyKind.PrintScreen
                             && (s.Status is HotkeyStatus.ReservedBySystem or HotkeyStatus.Busy or HotkeyStatus.Failed))
           && !states.Any(s => s.IsActive);

    private void OnPressed() => Pressed?.Invoke();

    public void Dispose()
    {
        foreach (var h in _registered.Values) h.Dispose();
        _registered.Clear();
        _hook?.Dispose();
        _hook = null;
    }
}

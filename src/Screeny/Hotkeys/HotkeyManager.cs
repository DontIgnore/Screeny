namespace Screeny.Hotkeys;

/// <summary>State of one hotkey entry after <see cref="HotkeyManager.Apply"/>.</summary>
public enum HotkeyStatus
{
    /// <summary>Works.</summary>
    Active,
    /// <summary>Entry disabled in settings.</summary>
    Disabled,
    /// <summary>Combo already used by another app (ERROR_HOTKEY_ALREADY_REGISTERED, 1409).</summary>
    Busy,
    /// <summary>Registration or hook install failed for other reason (code in <see cref="HotkeyState.Win32Error"/>).</summary>
    Failed,
    /// <summary>PrintScreen taken by Windows system screen capture: WM_HOTKEY won't arrive, skip registration.</summary>
    ReservedBySystem,
    /// <summary>Custom combo not recognized.</summary>
    Invalid,
    /// <summary>Not registered in this instance (<c>--dev</c> without <c>--enable-ll-hook</c>).</summary>
    NotAllowed,
}

/// <remarks>Stores problem kind and error code; text <see cref="Message"/> built on each read — in current
/// UI language, so after language change settings hints and notifications don't go stale.</remarks>
public sealed record HotkeyState(HotkeyBinding Binding, HotkeyStatus Status, int Win32Error = 0)
{
    public bool IsActive => Status == HotkeyStatus.Active;

    /// <summary>Problem text for user; null — no problem.</summary>
    public string? Message => Status switch
    {
        HotkeyStatus.ReservedBySystem => Localization.Loc.T("Hotkeys.PrintScreenReserved"),
        HotkeyStatus.Invalid => Localization.Loc.F("Hotkeys.Unrecognized", Binding.Spec),
        HotkeyStatus.Busy or HotkeyStatus.Failed when Binding.Kind == HotkeyKind.WinShiftS => Localization.Loc.F("Hotkeys.HookFailed", Win32Error),
        HotkeyStatus.Busy or HotkeyStatus.Failed when Binding.ToRegisterSpec() is { } spec => GlobalHotkey.DescribeFailure(spec, Win32Error),
        _ => null,
    };
}

/// <summary>Result of applying hotkey list: state of each entry and fallback hotkey if enabled.</summary>
/// <param name="FallbackErrorCode">Windows error code if fallback hotkey didn't register.</param>
public sealed record HotkeyReport(IReadOnlyList<HotkeyState> Entries, HotkeySpec? Fallback = null, int? FallbackErrorCode = null)
{
    /// <summary>Why fallback hotkey didn't enable — text in current language; null — no error.</summary>
    public string? FallbackError => FallbackErrorCode is { } code ? GlobalHotkey.DescribeFailure(HotkeySpec.Fallback, code) : null;

    public static readonly HotkeyReport Empty = new([]);

    /// <summary>Labels of all working hotkeys in list order, fallback last.</summary>
    public IReadOnlyList<string> ActiveNames =>
        [.. Entries.Where(e => e.IsActive).Select(e => e.Binding.DisplayName), .. Fallback is { } f ? [f.ToString()] : Array.Empty<string>()];

    public HotkeyState? Find(HotkeyKind kind) => Entries.FirstOrDefault(e => e.Binding.Kind == kind);

    /// <summary>All working hotkeys joined with / for menu and tray hints; an em dash if none work.</summary>
    public string ActiveText(int maxLength = 32) => FormatNames(ActiveNames, maxLength);

    /// <summary>Names joined with /; if longer than <paramref name="maxLength"/>, show the first followed by / and an ellipsis.</summary>
    public static string FormatNames(IReadOnlyList<string> names, int maxLength = 32)
    {
        if (names.Count == 0) return "—";
        var all = string.Join(" / ", names);
        return all.Length <= maxLength || names.Count == 1 ? all : names[0] + " / …";
    }

    /// <summary>
    /// Notification text about problems or null if nothing to report. Unavailable PrintScreen mentioned only
    /// when because of it fallback hotkey enabled or nothing works: if other combos work and PrintScreen
    /// is taken by Windows, user sees it in settings and doesn't get notification at every launch.
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

/// <summary>Result of <see cref="HotkeyManager.Apply"/>: on failure <see cref="Error"/> — entry causing rollback.</summary>
public sealed record HotkeyApplyResult(bool Success, HotkeyReport Report, HotkeyState? Error = null);

/// <summary>System operations needed by <see cref="HotkeyManager"/>; in tests substituted to not touch real hotkeys.</summary>
public interface IHotkeyBackend
{
    /// <summary>RegisterHotKey; <paramref name="pressed"/> called in UI thread.</summary>
    bool TryRegister(HotkeySpec spec, Action pressed, out IDisposable? handle, out int win32Error);

    /// <summary>Low-level Win+Shift+S hook; <paramref name="triggered"/> called in UI thread.</summary>
    bool TryInstallWinShiftSHook(Action triggered, out IDisposable? hook, out int win32Error);

    /// <summary>Whether Windows “Open screen snipping with PrintScreen” is enabled.</summary>
    bool IsPrintScreenReservedBySystem();
}

/// <summary>
/// Registers all enabled screenshot hotkeys: PrintScreen and custom combos via <c>RegisterHotKey</c> (each own id),
/// Win+Shift+S via low-level hook. Call from UI thread.
/// <para>
/// Fallback <see cref="HotkeySpec.Fallback"/> (<c>Ctrl+Alt+S</c>) registered automatically if PrintScreen enabled
/// but unavailable (system Windows capture or registration error), and no other hotkey works.
/// </para>
/// <para>
/// <see cref="Apply"/> is atomic in strict mode: already registered combos reused (re-registering own combo would give 1409),
/// New combinations are registered, and only if no <em>newly enabled</em> entry fails
/// are extra registrations removed. Otherwise, new registrations are removed and the previous state is preserved.
/// </para>
/// </summary>
public sealed class HotkeyManager(IHotkeyBackend backend, bool allowRegisterHotKey, bool allowHook) : IDisposable
{
    private readonly Dictionary<HotkeySpec, IDisposable> _registered = new();
    private IDisposable? _hook;
    private IReadOnlyList<HotkeyBinding> _applied = [];

    /// <summary>Any hotkey pressed (in UI thread).</summary>
    public event Action? Pressed;

    /// <summary>Report of last successful <see cref="Apply"/>.</summary>
    public HotkeyReport Report { get; private set; } = HotkeyReport.Empty;

    /// <summary>Applied list (last successful <see cref="Apply"/>).</summary>
    public IReadOnlyList<HotkeyBinding> Applied => _applied;

    public bool IsHookInstalled => _hook is not null;

    /// <param name="strict">
    /// true (settings save, wizard): if newly enabled entry didn't work (busy, error, hook not installed),
    /// nothing changes and error returned. Windows-unavailable PrintScreen is not an error — fallback works.
    /// false (startup): register everything that works, problems in report.
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

    /// <summary>First newly enabled entry that didn't work (Busy, Failed, Invalid).</summary>
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

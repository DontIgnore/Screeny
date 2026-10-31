namespace Screeny.Hotkeys;

/// <summary>Decision per keyboard event: swallow it and whether Win+Shift+S triggered.</summary>
public readonly record struct KeyDecision(bool Swallow, bool Triggered)
{
    public static readonly KeyDecision Pass = new(false, false);
}

/// <summary>
/// Recognizes Win+Shift+S in low-level hook event stream. Pure state machine without Win32 calls:
/// all methods called from one thread (hook thread).
/// <list type="bullet">
/// <item>S pressed with Win (left or right) and Shift (any) held, without Ctrl/Alt → trigger; S down swallowed,
/// so system Snipping Tool doesn't open. S auto-repeat until release swallowed without re-trigger,
/// S release also swallowed (even if Win already released).</item>
/// <item>Start menu: Windows opens it if no other key between Win press and release, and we swallowed S.
/// So after trigger hook thread inserts via SendInput “mask” — unassigned key
/// <see cref="MaskKey"/> (vkE8, like AutoHotkey) while Win is still held. Hook messages reach the thread earlier
/// than its own (PostThreadMessage), so Win release may arrive before mask insertion: such
/// release is swallowed and returned from <see cref="OnMaskSent"/>, to insert after mask.</item>
/// <item>Programmatically injected events (LLKHF_INJECTED: our mask, foreign SendInput) are skipped without state change.</item>
/// </list>
/// Known limitation: on secure desktop (UAC, lock screen) hook can't see events, and modifier release
/// may be lost — until next press of that key state is considered “held”.
/// </summary>
public sealed class WinShiftSDetector
{
    public const int VkS = 0x53, VkLWin = 0x5B, VkRWin = 0x5C;
    public const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12;
    public const int VkLShift = 0xA0, VkRShift = 0xA1, VkLControl = 0xA2, VkRControl = 0xA3, VkLMenu = 0xA4, VkRMenu = 0xA5;

    /// <summary>Unassigned key (VK 0xE8) inserted so Win release doesn't open Start menu.</summary>
    public const int MaskKey = 0xE8;

    private bool _lWin, _rWin, _lShift, _rShift, _lCtrl, _rCtrl, _lAlt, _rAlt;
    private bool _sSwallowed;
    private readonly List<int> _deferredWinUps = new();

    /// <summary>Triggered but mask not yet inserted: Win release currently held.</summary>
    public bool MaskPending { get; private set; }

    public bool WinDown => _lWin || _rWin;

    public KeyDecision OnKey(int vk, bool down, bool injected)
    {
        if (injected) return KeyDecision.Pass;
        switch (vk)
        {
            case VkLWin or VkRWin:
                if (vk == VkLWin) _lWin = down; else _rWin = down;
                if (!down && MaskPending)
                {
                    _deferredWinUps.Add(vk);
                    return new KeyDecision(true, false);
                }
                return KeyDecision.Pass;
            case VkLShift or VkShift: _lShift = down; return KeyDecision.Pass;
            case VkRShift: _rShift = down; return KeyDecision.Pass;
            case VkLControl or VkControl: _lCtrl = down; return KeyDecision.Pass;
            case VkRControl: _rCtrl = down; return KeyDecision.Pass;
            case VkLMenu or VkMenu: _lAlt = down; return KeyDecision.Pass;
            case VkRMenu: _rAlt = down; return KeyDecision.Pass;
            case VkS:
                return down ? OnSDown() : OnSUp();
            default:
                return KeyDecision.Pass;
        }
    }

    private KeyDecision OnSDown()
    {
        if (_sSwallowed) return new KeyDecision(true, false); // auto-repeat
        bool combo = WinDown && (_lShift || _rShift) && !(_lCtrl || _rCtrl || _lAlt || _rAlt);
        if (!combo) return KeyDecision.Pass;
        _sSwallowed = true;
        MaskPending = true;
        return new KeyDecision(true, true);
    }

    private KeyDecision OnSUp()
    {
        if (!_sSwallowed) return KeyDecision.Pass;
        _sSwallowed = false;
        return new KeyDecision(true, false);
    }

    /// <summary>
    /// Hook thread inserted mask. Returns held Win releases (VK) to insert after,
    /// — usually empty: user still holds Win.
    /// </summary>
    public IReadOnlyList<int> OnMaskSent()
    {
        MaskPending = false;
        if (_deferredWinUps.Count == 0) return [];
        var ups = _deferredWinUps.ToArray();
        _deferredWinUps.Clear();
        return ups;
    }
}

namespace Screeny.Hotkeys;

/// <summary>Решение по одному событию клавиатуры: проглотить ли его и сработал ли Win+Shift+S.</summary>
public readonly record struct KeyDecision(bool Swallow, bool Triggered)
{
    public static readonly KeyDecision Pass = new(false, false);
}

/// <summary>
/// Распознаёт Win+Shift+S в потоке событий низкоуровневого хука. Чистая машина состояний без вызовов Win32:
/// все методы вызываются из одного потока (поток хука).
/// <list type="bullet">
/// <item>S нажата при зажатых Win (левой или правой) и Shift (любой), без Ctrl и Alt → срабатывание; S down проглатывается,
/// чтобы не открылись системные «Ножницы». Автоповтор S до отпускания проглатывается без повторного срабатывания,
/// отпускание S тоже проглатывается (даже если Win уже отпущена).</item>
/// <item>Меню «Пуск»: Windows открывает его, если между нажатием и отпусканием Win не было другой клавиши, а S мы
/// проглотили. Поэтому после срабатывания поток хука вставляет через SendInput «маску» — неназначенную клавишу
/// <see cref="MaskKey"/> (vkE8, как AutoHotkey) — пока Win ещё зажата. Сообщения хука доставляются потоку раньше,
/// чем отправленные им самому (PostThreadMessage), поэтому отпускание Win может прийти до вставки маски: такое
/// отпускание проглатывается и отдаётся из <see cref="OnMaskSent"/>, чтобы вставить его после маски.</item>
/// <item>Вставленные программно события (LLKHF_INJECTED: наша маска, чужие SendInput) пропускаются без изменения состояния.</item>
/// </list>
/// Известное ограничение: на защищённом рабочем столе (UAC, экран блокировки) хук событий не видит, и отпускание
/// модификатора может потеряться — до следующего нажатия этой клавиши состояние считается «зажата».
/// </summary>
public sealed class WinShiftSDetector
{
    public const int VkS = 0x53, VkLWin = 0x5B, VkRWin = 0x5C;
    public const int VkShift = 0x10, VkControl = 0x11, VkMenu = 0x12;
    public const int VkLShift = 0xA0, VkRShift = 0xA1, VkLControl = 0xA2, VkRControl = 0xA3, VkLMenu = 0xA4, VkRMenu = 0xA5;

    /// <summary>Неназначенная клавиша (VK 0xE8), которую вставляют, чтобы отпускание Win не открыло «Пуск».</summary>
    public const int MaskKey = 0xE8;

    private bool _lWin, _rWin, _lShift, _rShift, _lCtrl, _rCtrl, _lAlt, _rAlt;
    private bool _sSwallowed;
    private readonly List<int> _deferredWinUps = new();

    /// <summary>Сработало, а маска ещё не вставлена: отпускание Win пока придерживается.</summary>
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
        if (_sSwallowed) return new KeyDecision(true, false); // автоповтор
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
    /// Поток хука вставил маску. Возвращает придержанные отпускания Win (VK), которые нужно вставить следом,
    /// — обычно пусто: пользователь ещё держит Win.
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

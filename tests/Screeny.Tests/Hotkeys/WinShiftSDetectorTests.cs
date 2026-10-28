using Screeny.Hotkeys;
using D = Screeny.Hotkeys.WinShiftSDetector;

namespace Screeny.Tests.Hotkeys;

/// <summary>Машина состояний Win+Shift+S; сам хук (SetWindowsHookEx) в тестах не ставится.</summary>
public class WinShiftSDetectorTests
{
    private static readonly KeyDecision Pass = KeyDecision.Pass;
    private static readonly KeyDecision Swallow = new(true, false);
    private static readonly KeyDecision Trigger = new(true, true);

    private static KeyDecision Down(D d, int vk) => d.OnKey(vk, down: true, injected: false);
    private static KeyDecision Up(D d, int vk) => d.OnKey(vk, down: false, injected: false);

    [Theory]
    [InlineData(D.VkLWin, D.VkLShift)]
    [InlineData(D.VkRWin, D.VkRShift)]
    [InlineData(D.VkLWin, D.VkRShift)]
    [InlineData(D.VkRWin, D.VkLShift)]
    [InlineData(D.VkLWin, D.VkShift)]
    public void WinShiftS_TriggersAndSwallowsS(int win, int shift)
    {
        var d = new D();
        Assert.Equal(Pass, Down(d, win));   // Win и Shift до системы доходят
        Assert.Equal(Pass, Down(d, shift));
        Assert.Equal(Trigger, Down(d, D.VkS));
        Assert.Equal(Swallow, Up(d, D.VkS));
        Assert.True(d.MaskPending);
    }

    [Fact]
    public void ShiftFirstThenWin_AlsoTriggers()
    {
        var d = new D();
        Down(d, D.VkLShift);
        Down(d, D.VkLWin);
        Assert.Equal(Trigger, Down(d, D.VkS));
    }

    [Fact]
    public void AutoRepeat_TriggersOnce()
    {
        var d = new D();
        Down(d, D.VkLWin); Down(d, D.VkLShift);
        Assert.Equal(Trigger, Down(d, D.VkS));
        Assert.Equal(Swallow, Down(d, D.VkS));
        Assert.Equal(Swallow, Down(d, D.VkS));
        Assert.Equal(Swallow, Up(d, D.VkS));
        Assert.Equal(Trigger, Down(d, D.VkS)); // новое нажатие при зажатых Win+Shift — новый снимок
    }

    [Fact]
    public void ModifierAutoRepeat_DoesNotBreakDetection()
    {
        var d = new D();
        Down(d, D.VkLWin); Down(d, D.VkLWin); Down(d, D.VkLShift); Down(d, D.VkLShift); Down(d, D.VkLShift);
        Assert.Equal(Trigger, Down(d, D.VkS));
    }

    [Theory]
    [InlineData(D.VkLControl)]
    [InlineData(D.VkRControl)]
    [InlineData(D.VkLMenu)]
    [InlineData(D.VkRMenu)]
    public void ExtraCtrlOrAlt_DoesNotTrigger(int extra)
    {
        var d = new D();
        Down(d, D.VkLWin); Down(d, D.VkLShift); Down(d, extra);
        Assert.Equal(Pass, Down(d, D.VkS));
        Assert.Equal(Pass, Up(d, D.VkS));
        Assert.False(d.MaskPending);
    }

    [Theory]
    [InlineData(new[] { D.VkLWin })]               // Win+S — поиск Windows
    [InlineData(new[] { D.VkLShift })]             // заглавная S
    [InlineData(new int[0])]
    public void WithoutWinAndShift_Passes(int[] held)
    {
        var d = new D();
        foreach (var vk in held) Down(d, vk);
        Assert.Equal(Pass, Down(d, D.VkS));
        Assert.Equal(Pass, Up(d, D.VkS));
    }

    [Fact]
    public void ReleasedModifiers_AreForgotten()
    {
        var d = new D();
        Down(d, D.VkLWin); Down(d, D.VkLShift);
        Up(d, D.VkLShift);
        Assert.Equal(Pass, Down(d, D.VkS));
        Up(d, D.VkS);
        Down(d, D.VkRShift); Up(d, D.VkLWin);
        Assert.Equal(Pass, Down(d, D.VkS));
    }

    [Fact]
    public void BothWins_ReleasingOneKeepsCombo()
    {
        var d = new D();
        Down(d, D.VkLWin); Down(d, D.VkRWin); Down(d, D.VkLShift);
        Up(d, D.VkLWin);
        Assert.Equal(Trigger, Down(d, D.VkS));
    }

    [Fact]
    public void InjectedEvents_AreIgnoredAndDoNotChangeState()
    {
        var d = new D();
        Assert.Equal(Pass, d.OnKey(D.VkLWin, true, injected: true));
        Assert.Equal(Pass, d.OnKey(D.VkLShift, true, injected: true));
        Assert.Equal(Pass, d.OnKey(D.VkS, true, injected: true));
        Assert.Equal(Pass, Down(d, D.VkS)); // инъекция не «зажала» Win и Shift
        Up(d, D.VkS);

        Down(d, D.VkLWin); Down(d, D.VkLShift);
        Assert.Equal(Pass, d.OnKey(D.VkS, true, injected: true)); // наша или чужая SendInput — не срабатывание
        Assert.Equal(Pass, d.OnKey(D.MaskKey, true, injected: true));
        Assert.Equal(Pass, d.OnKey(D.VkLWin, false, injected: true));
        Assert.Equal(Trigger, Down(d, D.VkS)); // физическая Win всё ещё зажата
    }

    [Fact]
    public void SUp_IsSwallowedEvenAfterWinAndShiftReleased()
    {
        var d = new D();
        Down(d, D.VkLWin); Down(d, D.VkLShift);
        Down(d, D.VkS);
        d.OnMaskSent();
        Assert.Equal(Pass, Up(d, D.VkLWin));
        Assert.Equal(Pass, Up(d, D.VkLShift));
        Assert.Equal(Swallow, Up(d, D.VkS)); // отпускание без нажатия в приложениях не появится
        Assert.Equal(Pass, Down(d, D.VkS));  // дальше S — обычная клавиша
    }

    [Fact]
    public void WinUp_AfterMaskSent_Passes()
    {
        var d = new D();
        Down(d, D.VkLWin); Down(d, D.VkLShift); Down(d, D.VkS); Up(d, D.VkS);
        Assert.Empty(d.OnMaskSent());
        Assert.False(d.MaskPending);
        Assert.Equal(Pass, Up(d, D.VkLShift));
        Assert.Equal(Pass, Up(d, D.VkLWin)); // маска уже между нажатием и отпусканием Win — «Пуск» не откроется
    }

    [Fact]
    public void WinUp_BeforeMaskSent_IsDeferredUntilAfterMask()
    {
        var d = new D();
        Down(d, D.VkRWin); Down(d, D.VkLShift); Down(d, D.VkS);
        Assert.Equal(Swallow, Up(d, D.VkRWin)); // гонка: отпускание пришло раньше обработки маски
        Assert.False(d.WinDown);
        Assert.Equal([D.VkRWin], d.OnMaskSent()); // вставить после маски
        Assert.False(d.MaskPending);
        Assert.Empty(d.OnMaskSent());
        Down(d, D.VkLWin);
        Assert.Equal(Pass, Up(d, D.VkLWin));
    }

    [Fact]
    public void BothWinsReleasedBeforeMask_BothDeferred()
    {
        var d = new D();
        Down(d, D.VkLWin); Down(d, D.VkRWin); Down(d, D.VkLShift); Down(d, D.VkS);
        Assert.Equal(Swallow, Up(d, D.VkLWin));
        Assert.Equal(Swallow, Up(d, D.VkRWin));
        Assert.Equal([D.VkLWin, D.VkRWin], d.OnMaskSent());
    }

    [Fact]
    public void OtherKeys_AlwaysPass()
    {
        var d = new D();
        Down(d, D.VkLWin); Down(d, D.VkLShift);
        Assert.Equal(Pass, Down(d, 0x41)); // A
        Assert.Equal(Pass, Up(d, 0x41));
        Assert.Equal(Pass, Down(d, D.MaskKey));
        Assert.False(d.MaskPending);
    }
}

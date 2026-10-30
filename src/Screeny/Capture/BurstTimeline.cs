namespace Screeny.Capture;

/// <summary>Что делать на очередном опросе клавиши (<see cref="BurstTimeline.Step"/>).</summary>
public enum BurstStep
{
    /// <summary>Ждать следующего опроса.</summary>
    Wait,
    /// <summary>Снять кадр серии.</summary>
    Capture,
    /// <summary>Короткое нажатие: открыть оверлей с первым кадром, как обычный снимок.</summary>
    OpenTap,
    /// <summary>Серия закончилась (клавишу отпустили или вышло время): открыть оверлей с кадрами.</summary>
    OpenBurst,
}

/// <summary>
/// Решение «нажатие или удержание» и ритм серии по времени и состоянию клавиши. Чистая логика: время и опрос клавиши
/// даёт вызывающий.
/// <list type="bullet">
/// <item>Клавишу отпустили раньше <see cref="HoldThreshold"/> — короткое нажатие (<see cref="BurstStep.OpenTap"/>).
/// Клавиша, которую не удаётся прочитать (уже отпущена на первом опросе), тоже считается нажатием: хуже, чем было,
/// не становится.</item>
/// <item>Держат дольше — серия: первый кадр серии сразу, дальше раз в <see cref="FrameInterval"/>, пока клавиша нажата,
/// кадры не кончились и не вышло <see cref="MaxHold"/>. Когда лимит кадров исчерпан, снимать перестаём, но ждём
/// отпускания: оверлей, открытый посреди удержания, забрал бы фокус у того, что снимают.</item>
/// <item><see cref="MaxHold"/> — страховка от «залипшей» клавиши: оверлей откроется и без отпускания.</item>
/// </list>
/// </summary>
public sealed class BurstTimeline
{
    public static readonly TimeSpan HoldThreshold = TimeSpan.FromMilliseconds(250);
    /// <summary>Шаг серии: около 10 кадров в секунду.</summary>
    public static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(100);
    public static readonly TimeSpan MaxHold = TimeSpan.FromSeconds(10);

    private bool _bursting;
    private TimeSpan? _lastFrame;

    /// <summary>Идёт серия (удержание уже признано).</summary>
    public bool IsBursting => _bursting;

    /// <param name="elapsed">Время от нажатия.</param>
    /// <param name="keyDown">Клавиша сейчас нажата.</param>
    /// <param name="full">Лимит кадров исчерпан: больше не снимать.</param>
    public BurstStep Step(TimeSpan elapsed, bool keyDown, bool full = false)
    {
        if (!_bursting)
        {
            if (!keyDown) return BurstStep.OpenTap;
            if (elapsed < HoldThreshold) return BurstStep.Wait;
            _bursting = true;
        }
        if (!keyDown || elapsed >= MaxHold) return BurstStep.OpenBurst;
        if (full) return BurstStep.Wait;
        if (_lastFrame is { } last && elapsed - last < FrameInterval) return BurstStep.Wait;
        _lastFrame = elapsed;
        return BurstStep.Capture;
    }
}

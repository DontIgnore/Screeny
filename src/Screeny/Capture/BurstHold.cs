using Screeny.Logging;

namespace Screeny.Capture;

/// <summary>
/// Решение по удержанию пришло (UI-поток), оверлей показывается. Это уже вне try/catch снимка: ошибка здесь иначе ушла бы
/// в DispatcherUnhandledException, оверлей не закрылся бы, а флаг снимка остался бы поднятым навсегда — все следующие
/// снимки молча не начинались бы. Действия над окном и приложением передаёт вызывающий, логика проверяется тестами.
/// </summary>
public static class BurstHold
{
    /// <param name="result">Итог удержания.</param>
    /// <param name="closed">Оверлей уже закрыт (сменились мониторы): показывать нечего, но память серии отпустить нужно —
    /// обработчик закрытия сработал раньше, чем стало известно, что это серия.</param>
    /// <param name="open">Кадры в оверлей и показ.</param>
    /// <param name="close">Закрыть оверлей после ошибки показа.</param>
    /// <param name="release">Отпустить память серии.</param>
    /// <param name="failed">Сбросить флаг снимка и сообщить об ошибке; вызывается и тогда, когда закрыть окно не удалось.</param>
    public static void Resolve(BurstResult result, bool closed, Action open, Action close, Action release, Action<Exception> failed)
    {
        if (closed)
        {
            if (result.IsBurst) release();
            return;
        }
        try { open(); }
        catch (Exception ex)
        {
            Log.Error("Showing the overlay after the hold failed", ex);
            try { close(); }
            catch (Exception closeEx) { Log.Error("Closing the overlay after a failed show failed", closeEx); }
            if (result.IsBurst) release();
            failed(ex);
        }
    }
}

/// <summary>
/// Серия, открытая по <see cref="BurstTimeline.MaxHold"/>, пока клавишу ещё держат: оверлей получает автоповтор клавиши
/// снимка (и её модификаторов) и принял бы его за свои сочетания. Пока клавиша нажата, оверлей пропускает её нажатия и
/// все автоповторы; отпустили — охрана снимается. Чистая логика: состояние клавиши даёт вызывающий.
/// </summary>
public static class HeldKeyGuard
{
    /// <param name="heldKey">Виртуальный код клавиши снимка, которую держали при показе оверлея.</param>
    /// <param name="key">Виртуальный код нажатой клавиши.</param>
    /// <param name="isRepeat">Нажатие — автоповтор.</param>
    /// <param name="heldStillDown">Клавиша снимка всё ещё нажата (GetAsyncKeyState).</param>
    /// <returns>Пропустить нажатие; оставить охрану.</returns>
    public static (bool Ignore, bool StayArmed) KeyDown(int heldKey, int key, bool isRepeat, bool heldStillDown)
        => heldStillDown ? (key == heldKey || isRepeat, true) : (false, false);
}

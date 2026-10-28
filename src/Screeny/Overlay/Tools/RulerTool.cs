using System.Windows;
using System.Windows.Input;
using Screeny.Annotations;

namespace Screeny.Overlay.Tools;

/// <summary>
/// Линейка: тянете от точки к точке — показывается одно временное измерение (<see cref="Measurement"/>).
/// Документ не меняется. Клик без протяжки или новый жест убирают прежнее измерение.
/// </summary>
/// <param name="constrain">Зажат ли Shift: шаг 45°.</param>
public sealed class RulerTool(Func<bool>? constrain = null) : ITool
{
    private Point _start;
    private bool _active;

    public RulerMeasurement? Measurement { get; private set; }
    /// <summary>Измерение появилось, изменилось или исчезло.</summary>
    public event Action? Changed;

    public Cursor Cursor => Cursors.Cross;
    public bool IsActive => _active;

    public void Down(Point px, AnnotationDocument doc, AnnotationStyle style)
    {
        _start = px;
        _active = true;
        Set(RulerMeasurement.Between(px, px, snap: false));
    }

    public void Move(Point px)
    {
        if (!_active) return;
        Set(RulerMeasurement.Between(_start, px, constrain?.Invoke() == true));
    }

    public void Up(Point px)
    {
        if (!_active) return;
        Move(px);
        _active = false;
        // Клик без протяжки — просто убрать прежнее измерение.
        if (Measurement is { Length: < 1 }) Set(null);
    }

    public void Cancel()
    {
        if (!_active) return;
        _active = false;
        Set(null);
    }

    /// <summary>Убрать измерение (Esc, смена инструмента, сброс выделения).</summary>
    public void Clear()
    {
        _active = false;
        Set(null);
    }

    private void Set(RulerMeasurement? m)
    {
        if (Measurement == m) return;
        Measurement = m;
        Changed?.Invoke();
    }
}

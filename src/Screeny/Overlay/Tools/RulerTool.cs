using System.Windows;
using System.Windows.Input;
using Screeny.Annotations;

namespace Screeny.Overlay.Tools;

/// <summary>
/// Ruler: drag from point to point — one temporary measurement shown (<see cref="Measurement"/>).
/// Document unchanged. Click without drag or new gesture removes previous measurement.
/// </summary>
/// <param name="constrain">Shift held: 45° step.</param>
public sealed class RulerTool(Func<bool>? constrain = null) : ITool
{
    private Point _start;
    private bool _active;

    public RulerMeasurement? Measurement { get; private set; }
    /// <summary>Measurement appeared, changed or disappeared.</summary>
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
        // Click without drag — just remove previous measurement.
        if (Measurement is { Length: < 1 }) Set(null);
    }

    public void Cancel()
    {
        if (!_active) return;
        _active = false;
        Set(null);
    }

    /// <summary>Remove measurement (Esc, tool change, selection reset).</summary>
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

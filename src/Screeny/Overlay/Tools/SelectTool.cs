using System.Windows;
using System.Windows.Input;
using Screeny.Annotations;
using Screeny.Annotations.Shapes;
using Screeny.Editing;

namespace Screeny.Overlay.Tools;

/// <summary>
/// Выбор и перемещение аннотаций. Во время перетаскивания фигура двигается напрямую,
/// а на отпускании уже применённый сдвиг записывается в undo одним шагом (без повторного применения).
/// С <paramref name="snap"/> края и центр фигуры прилипают к другим фигурам и к области: сдвиг считается от точки
/// нажатия (сырой сдвиг мыши + поправка прилипания), поэтому фигура не «залипает» и не накапливает ошибку.
/// </summary>
public sealed class SelectTool(Snapper? snap = null) : ITool
{
    private AnnotationDocument? _doc;
    private Annotation? _dragging;
    private Point _down;
    private Rect _startBounds;
    private Vector _total;

    public Cursor Cursor => Cursors.Arrow;
    public bool IsActive => _dragging is not null;

    public void Down(Point px, AnnotationDocument doc, AnnotationStyle style)
    {
        _doc = doc;
        doc.Selected = doc.HitTest(px);
        _dragging = doc.Selected;
        _down = px;
        _total = default;
        if (_dragging is not null)
        {
            _startBounds = _dragging.GeometryBounds;
            snap?.Begin(doc, exclude: _dragging);
        }
        // Эффект при перетаскивании считается по уменьшенной копии, полный пересчёт — после отпускания.
        if (_dragging is EffectAnnotation effect) effect.BeginLiveEdit();
        doc.NotifyChanged();
    }

    public void Move(Point px)
    {
        if (_dragging is null || _doc is null) return;
        var raw = px - _down;
        var desired = raw + (snap?.SnapRect(Rect.Offset(_startBounds, raw)) ?? default);
        var delta = desired - _total;
        if (delta.LengthSquared == 0) return;
        _dragging.Offset(delta);
        _total = desired;
        _doc.NotifyChanged();
    }

    public void Up(Point px)
    {
        if (_dragging is null || _doc is null) return;
        Move(px);
        var dragged = _dragging;
        var total = _total;
        _dragging = null;
        _total = default;
        snap?.End();
        if (dragged is EffectAnnotation effect) { effect.EndLiveEdit(); _doc.NotifyChanged(); }
        if (total.LengthSquared == 0) return;
        // Фигуру могли отменить (Ctrl+Z) посреди жеста: шаг не пишем, а сдвиг откатываем,
        // чтобы Redo вернул её на прежнее место.
        if (!_doc.RecordMove(dragged, total))
        {
            dragged.Offset(-total);
            _doc.NotifyChanged();
        }
    }

    public void Cancel()
    {
        if (_dragging is null) return;
        if (_total.LengthSquared > 0) _dragging.Offset(-_total);
        if (_dragging is EffectAnnotation effect) effect.EndLiveEdit();
        _dragging = null;
        _total = default;
        snap?.End();
        _doc?.NotifyChanged();
    }
}

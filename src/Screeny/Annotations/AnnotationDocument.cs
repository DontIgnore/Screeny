using System.Windows;
using System.Windows.Media;
using Screeny.Annotations.Shapes;

namespace Screeny.Annotations;

/// <summary>
/// Список аннотаций + undo. Фигура, которую сейчас рисуют, живёт в <see cref="Live"/>: она рисуется поверх
/// <see cref="Items"/>, но не участвует ни в списке, ни в undo, пока инструмент не вызовет Add на отпускании мыши.
/// </summary>
public sealed class AnnotationDocument
{
    private readonly List<Annotation> _items = new();
    private Annotation? _live;
    public IReadOnlyList<Annotation> Items => _items;
    public UndoStack Undo { get; } = new();
    public Annotation? Selected { get; set; }
    public event Action? Changed;

    /// <summary>«Живая» аннотация текущего жеста: рендерится поверх Items, но не входит в Items, hit-test и undo.</summary>
    public Annotation? Live
    {
        get => _live;
        set { if (ReferenceEquals(_live, value)) return; _live = value; Changed?.Invoke(); }
    }

    public AnnotationDocument() => Undo.Changed += () =>
    {
        if (Selected is not null && !_items.Contains(Selected)) Selected = null;
        Changed?.Invoke();
    };

    public void Add(Annotation a) => Undo.Execute(new AddAction(_items, a));
    public void Remove(Annotation a) { if (Selected == a) Selected = null; Undo.Execute(new RemoveAction(_items, a)); }
    public void Move(Annotation a, Vector delta) => Undo.Execute(new MoveAction(a, delta));

    /// <summary>
    /// Записывает в undo сдвиг, который уже применён к фигуре напрямую (перетаскивание).
    /// Возвращает false, если фигуры в документе нет (её отменили посреди жеста), — тогда шаг не записывается.
    /// </summary>
    public bool RecordMove(Annotation a, Vector delta)
    {
        if (!_items.Contains(a)) return false;
        Undo.Record(new MoveAction(a, delta));
        return true;
    }

    /// <summary>Смена цвета/толщины существующей аннотации — отдельный шаг undo.</summary>
    public void Restyle(Annotation a, AnnotationStyle style)
    {
        if (a.Style == style || !_items.Contains(a)) return;
        Undo.Execute(new StyleAction(a, a.Style, style));
    }

    public Annotation? HitTest(Point p)
    {
        for (int i = _items.Count - 1; i >= 0; i--) if (_items[i].HitTest(p)) return _items[i];
        return null;
    }

    /// <summary>
    /// Загружает готовые аннотации (документ из истории) без шагов undo: открытый документ не «грязный»,
    /// и Ctrl+Z не удаляет пометки, которые уже были на снимке.
    /// </summary>
    public void Load(IEnumerable<Annotation> annotations)
    {
        _items.AddRange(annotations);
        Changed?.Invoke();
    }

    /// <summary>Удаляет все аннотации одним шагом undo (отмена возвращает их в прежнем порядке). Пустой документ — без шага.</summary>
    public void RemoveAll()
    {
        if (_items.Count == 0) return;
        Selected = null;
        Undo.Execute(new RemoveAllAction(_items));
    }

    /// <summary>Удаляет всё без возможности отмены.</summary>
    public void Clear()
    {
        _items.Clear();
        _live = null;
        Selected = null;
        Undo.Clear();
    }

    public int NextNumber() => _items.OfType<NumberAnnotation>().Select(n => n.Number).DefaultIfEmpty(0).Max() + 1;
    public void NotifyChanged() => Changed?.Invoke();

    public void Render(DrawingContext dc, RenderContext ctx)
    {
        foreach (var a in _items) a.Render(dc, ctx);
        _live?.Render(dc, ctx);
    }

    private sealed class AddAction(List<Annotation> list, Annotation a) : IUndoableAction
    { public void Do() => list.Add(a); public void Undo() => list.Remove(a); }

    private sealed class RemoveAction(List<Annotation> list, Annotation a) : IUndoableAction
    { private int _index; public void Do() { _index = list.IndexOf(a); list.RemoveAt(_index); } public void Undo() => list.Insert(_index, a); }

    private sealed class RemoveAllAction(List<Annotation> list) : IUndoableAction
    {
        private Annotation[] _removed = [];
        public void Do() { _removed = list.ToArray(); list.Clear(); }
        public void Undo() => list.AddRange(_removed);
    }

    private sealed class MoveAction(Annotation a, Vector d) : IUndoableAction
    { public void Do() => a.Offset(d); public void Undo() => a.Offset(-d); }

    private sealed class StyleAction(Annotation a, AnnotationStyle before, AnnotationStyle after) : IUndoableAction
    { public void Do() => a.Style = after; public void Undo() => a.Style = before; }
}

using System.Windows;
using System.Windows.Media;
using Screeny.Annotations.Shapes;

namespace Screeny.Annotations;

/// <summary>
/// Annotation list + undo. The shape currently being drawn lives in <see cref="Live"/>: it is drawn on top of
/// <see cref="Items"/>, but is not in the list or undo until the tool calls Add on mouse up.
/// </summary>
public sealed class AnnotationDocument
{
    private readonly List<Annotation> _items = new();
    private Annotation? _live;
    public IReadOnlyList<Annotation> Items => _items;
    public UndoStack Undo { get; } = new();
    public Annotation? Selected { get; set; }
    public event Action? Changed;

    /// <summary>“Live” annotation of the current gesture: rendered over Items, but not part of Items, hit-test or undo.</summary>
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
    /// Records into undo the offset already applied directly to the shape (dragging).
    /// Returns false if the shape is not in the document (it was undone mid-gesture), then no step is recorded.
    /// </summary>
    public bool RecordMove(Annotation a, Vector delta)
    {
        if (!_items.Contains(a)) return false;
        Undo.Record(new MoveAction(a, delta));
        return true;
    }

    /// <summary>Changing color/thickness of an existing annotation is a separate undo step.</summary>
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
    /// Loads ready-made annotations (document from history) without undo steps: the opened document is not “dirty”,”
    /// and Ctrl+Z does not remove marks that were already on the screenshot.
    /// </summary>
    public void Load(IEnumerable<Annotation> annotations)
    {
        _items.AddRange(annotations);
        Changed?.Invoke();
    }

    /// <summary>Deletes all annotations in one undo step (undo restores them in original order). Empty document — no step.</summary>
    public void RemoveAll()
    {
        if (_items.Count == 0) return;
        Selected = null;
        Undo.Execute(new RemoveAllAction(_items));
    }

    /// <summary>Deletes everything without undo.</summary>
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

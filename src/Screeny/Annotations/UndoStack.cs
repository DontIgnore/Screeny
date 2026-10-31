namespace Screeny.Annotations;

public interface IUndoableAction { void Do(); void Undo(); }

public sealed class UndoStack
{
    private readonly record struct Entry(IUndoableAction Action, long Before, long After);
    private readonly Stack<Entry> _undo = new(), _redo = new();
    private long _nextRevision;
    /// <summary>Identifier of current state; undo/redo restore previous identifier, new branch gets its own.</summary>
    public long Revision { get; private set; }
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public event Action? Changed;

    public void Execute(IUndoableAction action) { action.Do(); Record(action); }
    /// <summary>Pushes already-applied action (e.g., dragging that happened directly) onto stack without calling Do.</summary>
    public void Record(IUndoableAction action)
    {
        var entry = new Entry(action, Revision, ++_nextRevision);
        _undo.Push(entry); _redo.Clear(); Revision = entry.After; Changed?.Invoke();
    }
    public void Undo() { if (!CanUndo) return; var a = _undo.Pop(); a.Action.Undo(); _redo.Push(a); Revision = a.Before; Changed?.Invoke(); }
    public void Redo() { if (!CanRedo) return; var a = _redo.Pop(); a.Action.Do(); _undo.Push(a); Revision = a.After; Changed?.Invoke(); }
    public void Clear() { _undo.Clear(); _redo.Clear(); Revision = ++_nextRevision; Changed?.Invoke(); }
}

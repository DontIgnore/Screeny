using Screeny.Annotations;

namespace Screeny.Tests.Annotations;

public class UndoStackTests
{
    private sealed class Counter : IUndoableAction
    {
        public int Value; public void Do() => Value++; public void Undo() => Value--;
    }

    [Fact]
    public void Execute_RunsAction_AndEnablesUndo()
    {
        var s = new UndoStack(); var a = new Counter();
        s.Execute(a);
        Assert.Equal(1, a.Value); Assert.True(s.CanUndo); Assert.False(s.CanRedo);
    }

    [Fact]
    public void Undo_Redo_RoundTrip()
    {
        var s = new UndoStack(); var a = new Counter();
        s.Execute(a); s.Undo();
        Assert.Equal(0, a.Value); Assert.True(s.CanRedo);
        s.Redo(); Assert.Equal(1, a.Value);
    }

    [Fact]
    public void Execute_AfterUndo_ClearsRedo()
    {
        var s = new UndoStack(); var a = new Counter();
        s.Execute(a); s.Undo(); s.Execute(new Counter());
        Assert.False(s.CanRedo);
    }

    [Fact]
    public void Changed_RaisedOnEveryMutation()
    {
        var s = new UndoStack(); int n = 0; s.Changed += () => n++;
        s.Execute(new Counter()); s.Undo(); s.Redo();
        Assert.Equal(3, n);
    }

    [Fact]
    public void UndoStack_Record_PushesWithoutExecuting()
    {
        var s = new UndoStack(); var a = new Counter(); int n = 0;
        s.Execute(new Counter()); s.Undo(); // есть redo
        s.Changed += () => n++;
        s.Record(a);
        Assert.Equal(0, a.Value);
        Assert.True(s.CanUndo); Assert.False(s.CanRedo);
        Assert.Equal(1, n);
        s.Undo(); Assert.Equal(-1, a.Value);
    }

    [Fact]
    public void Revision_ReturnsToSavedStateOnUndoAndRedo_AndDistinguishesBranches()
    {
        var stack = new UndoStack();
        var initial = stack.Revision;
        stack.Execute(new Counter());
        var saved = stack.Revision;
        stack.Execute(new Counter());
        var discarded = stack.Revision;
        stack.Undo();
        Assert.Equal(saved, stack.Revision);
        stack.Undo();
        Assert.Equal(initial, stack.Revision);
        stack.Redo();
        Assert.Equal(saved, stack.Revision);
        stack.Execute(new Counter());
        Assert.NotEqual(discarded, stack.Revision);
        stack.Clear();
        Assert.NotEqual(initial, stack.Revision);
    }
}

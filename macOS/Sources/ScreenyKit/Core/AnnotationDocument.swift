import CoreGraphics
import Foundation

public protocol UndoableAction { func `do`(); func undo() }

public final class UndoStack {
    private struct Entry { let action: UndoableAction; let before: Int; let after: Int }
    private var undoStack: [Entry] = [], redoStack: [Entry] = []
    private var nextRevision = 0
    /// Id of the current state; undo/redo return the previous id, a new branch gets its own.
    public private(set) var revision = 0
    public var canUndo: Bool { !undoStack.isEmpty }
    public var canRedo: Bool { !redoStack.isEmpty }
    public var changed: (() -> Void)?

    public func execute(_ a: UndoableAction) { a.do(); record(a) }
    /// Pushes an action that has already been applied (a drag).
    public func record(_ a: UndoableAction) {
        nextRevision += 1
        let e = Entry(action: a, before: revision, after: nextRevision)
        undoStack.append(e); redoStack.removeAll(); revision = e.after; changed?()
    }
    public func undo() { guard let e = undoStack.popLast() else { return }; e.action.undo(); redoStack.append(e); revision = e.before; changed?() }
    public func redo() { guard let e = redoStack.popLast() else { return }; e.action.do(); undoStack.append(e); revision = e.after; changed?() }
    public func clear() { undoStack.removeAll(); redoStack.removeAll(); nextRevision += 1; revision = nextRevision; changed?() }
}

/// Annotation list + undo (Annotations/AnnotationDocument.cs). The shape being drawn lives in `live`.
public final class AnnotationDocument {
    public private(set) var items: [Annotation] = []
    public let undo = UndoStack()
    public var selected: Annotation?
    private var observers: [() -> Void] = []
    public var live: Annotation? { didSet { if live !== oldValue { notify() } } }

    public init() {
        undo.changed = { [weak self] in
            guard let self else { return }
            if let s = self.selected, !self.contains(s) { self.selected = nil }
            self.notify()
        }
    }

    public func onChange(_ f: @escaping () -> Void) { observers.append(f) }
    public func notify() { observers.forEach { $0() } }
    public func contains(_ a: Annotation) -> Bool { items.contains { $0 === a } }

    public func add(_ a: Annotation) { undo.execute(Action(do: { [unowned self] in items.append(a) }, undo: { [unowned self] in items.removeAll { $0 === a } })) }

    public func remove(_ a: Annotation) {
        if selected === a { selected = nil }
        var index = 0
        undo.execute(Action(do: { [unowned self] in index = items.firstIndex { $0 === a } ?? 0; items.remove(at: index) },
                            undo: { [unowned self] in items.insert(a, at: min(index, items.count)) }))
    }

    public func move(_ a: Annotation, _ d: CGVector) { undo.execute(Action(do: { a.offset(d) }, undo: { a.offset(-d) })) }

    /// Records a drag already applied; false if the shape is gone (undone mid-gesture).
    @discardableResult public func recordMove(_ a: Annotation, _ d: CGVector) -> Bool {
        guard contains(a) else { return false }
        undo.record(Action(do: { a.offset(d) }, undo: { a.offset(-d) }))
        return true
    }

    public func restyle(_ a: Annotation, _ style: AnnotationStyle) {
        guard a.style != style, contains(a) else { return }
        let before = a.style
        undo.execute(Action(do: { a.style = style }, undo: { a.style = before }))
    }

    public func hitTest(_ p: CGPoint) -> Annotation? { items.last { $0.hitTest(p) } }

    /// Loads existing annotations without undo steps (re-editing from history).
    public func load(_ list: [Annotation]) { items.append(contentsOf: list); notify() }

    public func removeAll() {
        guard !items.isEmpty else { return }
        selected = nil
        var removed: [Annotation] = []
        undo.execute(Action(do: { [unowned self] in removed = items; items.removeAll() }, undo: { [unowned self] in items.append(contentsOf: removed) }))
    }

    public func clear() { items.removeAll(); live = nil; selected = nil; undo.clear() }

    public func nextNumber() -> Int { (items.compactMap { ($0 as? NumberAnnotation)?.number }.max() ?? 0) + 1 }

    public func render(_ ctx: CGContext, _ rc: RenderContext) {
        for a in items { a.render(ctx, rc) }
        live?.render(ctx, rc)
    }

    private struct Action: UndoableAction {
        let doF: () -> Void, undoF: () -> Void
        init(do d: @escaping () -> Void, undo u: @escaping () -> Void) { doF = d; undoF = u }
        func `do`() { doF() }
        func undo() { undoF() }
    }
}

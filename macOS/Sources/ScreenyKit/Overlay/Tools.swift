import AppKit

/// A drawing tool driven by mouse down/move/up in screenshot pixels (Overlay/Tools/*.cs).
protocol Tool: AnyObject {
    func down(_ p: CGPoint, _ doc: AnnotationDocument, _ style: AnnotationStyle)
    func move(_ p: CGPoint)
    func up(_ p: CGPoint)
    func cancel()
    var isActive: Bool { get }
    var cursor: NSCursor { get }
}

final class SelectTool: Tool {
    private var doc: AnnotationDocument?
    private var dragging: Annotation?
    private var downAt = CGPoint.zero
    private var startBounds = CGRect.null
    private var total = CGVector.zero
    let snap: Snapper?
    init(snap: Snapper?) { self.snap = snap }

    var cursor: NSCursor { .arrow }
    var isActive: Bool { dragging != nil }

    func down(_ p: CGPoint, _ doc: AnnotationDocument, _ style: AnnotationStyle) {
        self.doc = doc
        doc.selected = doc.hitTest(p)
        dragging = doc.selected
        downAt = p; total = .zero
        if let d = dragging { startBounds = d.geometryBounds; snap?.begin(doc, exclude: d) }
        (dragging as? EffectAnnotation)?.beginLiveEdit()
        doc.notify()
    }

    func move(_ p: CGPoint) {
        guard let d = dragging, let doc else { return }
        let raw = p - downAt
        let desired = raw + (snap?.snapRect(startBounds.offset(raw)) ?? .zero)
        let delta = desired - total
        if delta.lengthSquared == 0 { return }
        d.offset(delta)
        total = desired
        doc.notify()
    }

    func up(_ p: CGPoint) {
        guard let d = dragging, let doc else { return }
        move(p)
        let t = total
        dragging = nil; total = .zero
        snap?.end()
        if let e = d as? EffectAnnotation { e.endLiveEdit(); doc.notify() }
        if t.lengthSquared == 0 { return }
        if !doc.recordMove(d, t) { d.offset(-t); doc.notify() }
    }

    func cancel() {
        guard let d = dragging else { return }
        if total.lengthSquared > 0 { d.offset(-total) }
        (d as? EffectAnnotation)?.endLiveEdit()
        dragging = nil; total = .zero
        snap?.end()
        doc?.notify()
    }
}

final class ShapeTool: Tool {
    let kind: ToolKind
    let constrain: () -> Bool
    let snap: Snapper?
    private var doc: AnnotationDocument?
    private var shape: Annotation?
    private var start = CGPoint.zero

    init(_ kind: ToolKind, constrain: @escaping () -> Bool, snap: Snapper?) { self.kind = kind; self.constrain = constrain; self.snap = snap }
    var cursor: NSCursor { .crosshair }
    var isActive: Bool { shape != nil }

    func down(_ p0: CGPoint, _ doc: AnnotationDocument, _ style: AnnotationStyle) {
        snap?.begin(doc)
        var p = p0
        if let snap, !constrain() { p = snap.snapPoint(p) }
        self.doc = doc; start = p
        let r = CGRect(origin: p, size: .zero)
        switch kind {
        case .rect: shape = RectAnnotation(r, style: style)
        case .ellipse: shape = EllipseAnnotation(r, style: style)
        case .line: shape = LineAnnotation(p, p, style: style)
        case .arrow: shape = ArrowAnnotation(p, p, style: style)
        case .pixelate: shape = PixelateAnnotation(r, style: style)
        case .blur: shape = BlurAnnotation(r, style: style)
        default: return
        }
        doc.live = shape
        (shape as? EffectAnnotation)?.beginLiveEdit()
    }

    func move(_ p0: CGPoint) {
        guard let shape, let doc else { return }
        let shift = constrain()
        var p = p0
        if shift { snap?.hide() } else if let snap { p = snap.snapPoint(p) }
        if let line = shape as? LineAnnotation { line.end = shift ? Self.snapAngle(start, p) : p }
        else if let box = shape as? BoxAnnotation { box.rect = shift ? Self.square(start, p) : CGRect(corner: start, p) }
        doc.notify()
    }

    func up(_ p: CGPoint) {
        guard let s = shape, let doc else { return }
        move(p)
        shape = nil
        snap?.end()
        doc.live = nil
        (s as? EffectAnnotation)?.endLiveEdit()
        let b = (s as? LineAnnotation).map { CGRect(corner: $0.start, $0.end) } ?? s.geometryBounds
        // An accidental click without dragging is dropped.
        if b.width < 2 && b.height < 2 { doc.notify(); return }
        doc.add(s)
    }

    func cancel() {
        guard let s = shape else { return }
        (s as? EffectAnnotation)?.endLiveEdit()
        shape = nil
        snap?.end()
        doc?.live = nil
    }

    static func square(_ a: CGPoint, _ b: CGPoint) -> CGRect {
        let side = max(abs(b.x - a.x), abs(b.y - a.y))
        return CGRect(corner: a, CGPoint(x: a.x + (b.x - a.x < 0 ? -1 : 1) * side, y: a.y + (b.y - a.y < 0 ? -1 : 1) * side))
    }

    static func snapAngle(_ a: CGPoint, _ b: CGPoint) -> CGPoint {
        let v = b - a
        let angle = (atan2(v.dy, v.dx) / (.pi / 4)).rounded() * (.pi / 4)
        return a + CGVector(dx: cos(angle), dy: sin(angle)) * v.length
    }
}

final class PenTool: Tool {
    let highlighter: Bool
    private var doc: AnnotationDocument?
    private var stroke: PenAnnotation?
    private var last = CGPoint.zero
    init(highlighter: Bool) { self.highlighter = highlighter }
    var cursor: NSCursor { .crosshair }
    var isActive: Bool { stroke != nil }

    func down(_ p: CGPoint, _ doc: AnnotationDocument, _ style: AnnotationStyle) {
        self.doc = doc; last = p
        stroke = highlighter ? HighlighterAnnotation(p, style: style) : PenAnnotation(p, style: style)
        doc.live = stroke
    }

    func move(_ p: CGPoint) {
        guard let stroke, (p - last).length >= 1.5 else { return }
        stroke.addPoint(p); last = p
        doc?.notify()
    }

    func up(_ p: CGPoint) {
        guard let s = stroke, let doc else { return }
        move(p)
        stroke = nil
        doc.live = nil
        doc.add(s)
    }

    func cancel() { guard stroke != nil else { return }; stroke = nil; doc?.live = nil }
}

final class NumberTool: Tool {
    let snap: Snapper?
    private var doc: AnnotationDocument?
    private var label: NumberAnnotation?
    private var center = CGPoint.zero
    init(snap: Snapper?) { self.snap = snap }
    var cursor: NSCursor { .pointingHand }
    var isActive: Bool { label != nil }

    func down(_ p: CGPoint, _ doc: AnnotationDocument, _ style: AnnotationStyle) {
        self.doc = doc
        snap?.begin(doc)
        label = NumberAnnotation(p, number: doc.nextNumber(), style: style)
        center = p
        place(p)
        doc.live = label
    }

    func move(_ p: CGPoint) { guard label != nil else { return }; place(p); doc?.notify() }

    func up(_ p: CGPoint) {
        guard let l = label, let doc else { return }
        place(p)
        label = nil
        snap?.end()
        doc.live = nil
        doc.add(l)
    }

    func cancel() { guard label != nil else { return }; label = nil; snap?.end(); doc?.live = nil }

    private func place(_ p: CGPoint) {
        guard let l = label else { return }
        let at = p + (snap?.snapRect(l.geometryBounds.offset(p - center)) ?? .zero)
        l.offset(at - center)
        center = at
    }
}

final class TextTool: Tool {
    let snap: Snapper?
    var editRequested: ((CGPoint, AnnotationStyle) -> Void)?
    init(snap: Snapper?) { self.snap = snap }
    var cursor: NSCursor { .iBeam }
    var isActive: Bool { false }

    func down(_ p0: CGPoint, _ doc: AnnotationDocument, _ style: AnnotationStyle) {
        var p = p0
        if let snap { snap.begin(doc); p = snap.snapPoint(p); snap.end() }
        editRequested?(p, style)
    }
    func move(_ p: CGPoint) {}
    func up(_ p: CGPoint) {}
    func cancel() {}
}

final class RulerTool: Tool {
    let constrain: () -> Bool
    private var start = CGPoint.zero
    private var active = false
    private(set) var measurement: RulerMeasurement?
    var changed: (() -> Void)?
    init(constrain: @escaping () -> Bool) { self.constrain = constrain }
    var cursor: NSCursor { .crosshair }
    var isActive: Bool { active }

    func down(_ p: CGPoint, _ doc: AnnotationDocument, _ style: AnnotationStyle) {
        start = p; active = true
        set(.between(p, p, snap: false))
    }
    func move(_ p: CGPoint) { guard active else { return }; set(.between(start, p, snap: constrain())) }
    func up(_ p: CGPoint) {
        guard active else { return }
        move(p); active = false
        if let m = measurement, m.length < 1 { set(nil) }
    }
    func cancel() { guard active else { return }; active = false; set(nil) }
    func clear() { active = false; set(nil) }
    private func set(_ m: RulerMeasurement?) { if m == measurement { return }; measurement = m; changed?() }
}

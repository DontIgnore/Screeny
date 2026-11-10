import CoreGraphics
import Foundation

public enum ToolKind: Int, CaseIterable { case select, rect, ellipse, line, arrow, pen, highlighter, text, number, pixelate, blur, ruler }

public enum OptionsSectionKind { case shape, arrow, text, beautify }
public enum PaintTarget { case color, fill, background }

/// Options sections for the tool or the selected shape (Overlay/OptionsSections.cs).
public enum OptionsSections {
    public static func sections(tool: ToolKind, selected: Annotation?, beautify: Bool) -> [OptionsSectionKind] {
        var list: [OptionsSectionKind] = []
        if let main = selected.map(forAnnotation) ?? forTool(tool) { list.append(main) }
        if beautify { list.append(.beautify) }
        return list
    }
    public static func forTool(_ t: ToolKind) -> OptionsSectionKind? {
        switch t { case .rect, .ellipse: return .shape; case .arrow: return .arrow; case .text: return .text; default: return nil }
    }
    public static func forAnnotation(_ a: Annotation) -> OptionsSectionKind? {
        switch a { case is RectAnnotation, is EllipseAnnotation: return .shape; case is ArrowAnnotation: return .arrow; case is TextAnnotation: return .text; default: return nil }
    }
    public static func targets(_ k: OptionsSectionKind) -> [PaintTarget] {
        switch k { case .shape: return [.color, .fill]; case .text: return [.color, .background]; default: return [] }
    }
    public static func effectiveTarget(_ sections: [OptionsSectionKind], _ active: PaintTarget) -> PaintTarget {
        active == .color || sections.contains { targets($0).contains(active) } ? active : .color
    }
}

/// Parameters of new shapes beyond palette and thickness (Overlay/StyleOptions.cs).
public final class StyleOptions {
    public static let fillOpacitySteps = [20, 40, 60, 80, 100]
    public static let defaultTextBackground = RGBA(0x1C, 0x19, 0x17)

    public var strokeEnabled = true
    public var fillEnabled = false
    public var fillColor = RGBA.red
    public var fillOpacity = 40
    public var arrow = ArrowStyle.normal
    public var textBackgroundEnabled = false
    public var textBackgroundColor = StyleOptions.defaultTextBackground
    public var textAlign = TextAlign.left
    public var activeTarget = PaintTarget.color

    public init() {}

    public static func from(_ s: AppSettings, palette: RGBA) -> StyleOptions {
        let o = StyleOptions()
        o.strokeEnabled = s.lastStrokeEnabled
        o.fillEnabled = s.lastFillEnabled
        o.fillColor = palette.opaque
        o.fillOpacity = clamp(s.lastFillOpacity, AppSettings.minFillOpacity, AppSettings.maxFillOpacity)
        o.arrow = s.lastArrowStyle
        o.textBackgroundEnabled = s.lastTextBackground
        o.textAlign = s.lastTextAlign
        if !o.strokeEnabled && !o.fillEnabled { o.strokeEnabled = true }
        return o
    }

    public func save(to s: AppSettings) {
        s.lastStrokeEnabled = strokeEnabled; s.lastFillEnabled = fillEnabled; s.lastFillOpacity = fillOpacity
        s.lastArrowStyle = arrow; s.lastTextBackground = textBackgroundEnabled; s.lastTextAlign = textAlign
    }

    public func style(for tool: ToolKind, color: RGBA, thickness: Double) -> AnnotationStyle {
        var s = AnnotationStyle(color: color, thickness: thickness)
        switch tool {
        case .rect, .ellipse:
            s.strokeEnabled = strokeEnabled || !fillEnabled
            s.fill = fillEnabled ? fillColor.withOpacity(fillOpacity) : nil
        case .arrow: s.arrow = arrow
        case .text: s.textBackground = textBackgroundEnabled ? textBackgroundColor : nil; s.textAlign = textAlign
        default: break
        }
        return s
    }

    public func absorb(_ style: AnnotationStyle, _ section: OptionsSectionKind) {
        switch section {
        case .shape:
            strokeEnabled = style.strokeEnabled || style.fill == nil
            fillEnabled = style.fill != nil
            if let f = style.fill { fillColor = f.opaque; fillOpacity = clamp(f.opacityPercent, AppSettings.minFillOpacity, AppSettings.maxFillOpacity) }
        case .arrow: arrow = style.arrow
        case .text:
            textBackgroundEnabled = style.textBackground != nil
            if let bg = style.textBackground { textBackgroundColor = bg }
            textAlign = style.textAlign
        case .beautify: break
        }
    }

    public func toggleStroke(_ s: AnnotationStyle) -> AnnotationStyle { s.withStroke(!s.strokeEnabled) }
    public func toggleFill(_ s: AnnotationStyle) -> AnnotationStyle { s.withFill(s.fill == nil ? fillColor.withOpacity(fillOpacity) : nil) }

    public func nextFillOpacity(_ s: AnnotationStyle) -> AnnotationStyle {
        let current = s.fill?.opacityPercent ?? fillOpacity
        let next = Self.fillOpacitySteps.first { $0 > current + 1 } ?? Self.fillOpacitySteps[0]
        let rgb = s.fill?.opaque ?? fillColor
        fillOpacity = next
        return s.withFill(rgb.withOpacity(next))
    }

    public func toggleTextBackground(_ s: AnnotationStyle) -> AnnotationStyle {
        var r = s; r.textBackground = s.textBackground == nil ? textBackgroundColor : nil; return r
    }

    public func paint(_ s: AnnotationStyle, _ target: PaintTarget, _ c: RGBA) -> AnnotationStyle {
        var r = s
        switch target {
        case .fill: return s.withFill(c.withOpacity(s.fill?.opacityPercent ?? fillOpacity))
        case .background: r.textBackground = c.opaque
        case .color: r.color = c.opaque; r.strokeEnabled = true
        }
        return r
    }

    public static func swatchColor(_ s: AnnotationStyle, _ target: PaintTarget) -> RGBA? {
        switch target { case .fill: return s.fill?.opaque; case .background: return s.textBackground; case .color: return s.color }
    }
}

// MARK: - Shape snapping (Editing/SnapEngine.cs)

public enum GuideOrientation { case vertical, horizontal }

public struct SnapGuide: Equatable {
    public var orientation: GuideOrientation
    public var position, start, end: Double
}

public enum SnapEngine {
    static let same = 1e-3

    public static func snap(_ moving: CGRect, _ targets: [CGRect], threshold: Double) -> (CGVector, [SnapGuide]) {
        guard !moving.isNull, !targets.isEmpty, threshold > 0 else { return (.zero, []) }
        let dx = nearest(moving, targets, vertical: true, threshold)
        let dy = nearest(moving, targets, vertical: false, threshold)
        if dx == nil && dy == nil { return (.zero, []) }
        let snapped = moving.offsetBy(dx: dx ?? 0, dy: dy ?? 0)
        var guides: [SnapGuide] = []
        if dx != nil { addGuides(&guides, snapped, targets, vertical: true) }
        if dy != nil { addGuides(&guides, snapped, targets, vertical: false) }
        return (CGVector(dx: dx ?? 0, dy: dy ?? 0), guides)
    }

    static func lines(_ r: CGRect, _ vertical: Bool) -> [Double] {
        vertical ? [r.minX, r.minX + r.width / 2, r.maxX] : [r.minY, r.minY + r.height / 2, r.maxY]
    }

    static func nearest(_ moving: CGRect, _ targets: [CGRect], vertical: Bool, _ threshold: Double) -> Double? {
        let m = lines(moving, vertical)
        var best: Double?
        for t in targets where !t.isNull {
            for v in lines(t, vertical) {
                for mm in m {
                    let d = v - mm
                    if abs(d) <= threshold && (best == nil || abs(d) < abs(best!)) { best = d }
                }
            }
        }
        return best
    }

    static func addGuides(_ guides: inout [SnapGuide], _ snapped: CGRect, _ targets: [CGRect], vertical: Bool) {
        let o: GuideOrientation = vertical ? .vertical : .horizontal
        for m in lines(snapped, vertical) {
            if guides.contains(where: { $0.orientation == o && abs($0.position - m) < same }) { continue }
            var start = vertical ? snapped.minY : snapped.minX, end = vertical ? snapped.maxY : snapped.maxX
            var matched = false
            for t in targets where !t.isNull {
                let tl = lines(t, vertical)
                guard tl.contains(where: { abs($0 - m) < same }) else { continue }
                matched = true
                start = min(start, vertical ? t.minY : t.minX)
                end = max(end, vertical ? t.maxY : t.maxX)
            }
            if matched { guides.append(SnapGuide(orientation: o, position: m, start: start, end: end)) }
        }
    }
}

public final class Snapper {
    private var targets: [CGRect] = []
    public private(set) var guides: [SnapGuide] = []
    public var guidesChanged: (() -> Void)?
    let enabled: () -> Bool, threshold: () -> Double, area: () -> CGRect

    public init(enabled: @escaping () -> Bool, threshold: @escaping () -> Double, area: @escaping () -> CGRect) {
        self.enabled = enabled; self.threshold = threshold; self.area = area
    }

    public func begin(_ doc: AnnotationDocument, exclude: Annotation? = nil) {
        begin(doc.items.filter { $0 !== exclude }.map(\.geometryBounds))
    }

    public func begin(_ others: [CGRect]) {
        var list = others.filter { !$0.isNull }
        let a = area()
        if !a.isNull && a.width > 0 && a.height > 0 { list.append(a) }
        targets = list
        set([])
    }

    public func snapRect(_ moving: CGRect) -> CGVector {
        if !enabled() { set([]); return .zero }
        let (off, g) = SnapEngine.snap(moving, targets, threshold: threshold())
        set(g)
        return off
    }

    public func snapPoint(_ p: CGPoint) -> CGPoint { p + snapRect(CGRect(origin: p, size: .zero)) }
    public func hide() { set([]) }
    public func end() { targets = []; set([]) }

    private func set(_ g: [SnapGuide]) {
        if g == guides { return }
        guides = g
        guidesChanged?()
    }
}

// MARK: - Selection frame (Overlay/SelectionModel.cs)

public enum Handle { case none, move, topLeft, top, topRight, right, bottomRight, bottom, bottomLeft, left }
public enum EdgeAxis { case vertical, horizontal }
public typealias EdgeSnap = (EdgeAxis, Int, Int) -> Int?

public final class SelectionModel {
    public let bounds: PixelRect
    public var rect: PixelRect = .empty
    public var hasSelection: Bool { !rect.isEmpty }
    private var dragHandle = Handle.none
    private var dragStart = PixelPoint(x: 0, y: 0)
    private var dragOrigin = PixelRect.empty

    public init(bounds: PixelRect) { self.bounds = bounds }

    public static func fromPoints(_ a: PixelPoint, _ b: PixelPoint, snap: EdgeSnap? = nil) -> PixelRect {
        let r = PixelRect.fromCorners(min(a.x, b.x), min(a.y, b.y), max(a.x, b.x) + 1, max(a.y, b.y) + 1)
        guard let snap else { return r }
        var x1 = r.x, x2 = r.right, y1 = r.y, y2 = r.bottom
        if b.x >= a.x { x2 = snapIfValid(snap, .vertical, x2, b.y, lo: x1 + 1, hi: .max) } else { x1 = snapIfValid(snap, .vertical, x1, b.y, lo: .min, hi: x2 - 1) }
        if b.y >= a.y { y2 = snapIfValid(snap, .horizontal, y2, b.x, lo: y1 + 1, hi: .max) } else { y1 = snapIfValid(snap, .horizontal, y1, b.x, lo: .min, hi: y2 - 1) }
        return .fromCorners(x1, y1, x2, y2)
    }

    static func snapIfValid(_ snap: EdgeSnap, _ axis: EdgeAxis, _ edge: Int, _ cross: Int, lo: Int, hi: Int) -> Int {
        if let s = snap(axis, edge, cross), s >= lo, s <= hi { return s }
        return edge
    }

    public func nudge(_ dx: Int, _ dy: Int) {
        let r = rect.offset(dx, dy)
        rect = PixelRect(x: clamp(r.x, bounds.x, bounds.right - r.width), y: clamp(r.y, bounds.y, bounds.bottom - r.height), width: r.width, height: r.height)
    }

    public func resize(_ dw: Int, _ dh: Int) {
        rect = PixelRect(x: rect.x, y: rect.y, width: clamp(rect.width + dw, 1, bounds.right - rect.x), height: clamp(rect.height + dh, 1, bounds.bottom - rect.y))
    }

    public func hitTest(_ p: PixelPoint, tolerance t: Int) -> Handle {
        guard hasSelection else { return .none }
        let r = rect
        let nearL = abs(p.x - r.x) <= t, nearR = abs(p.x - r.right) <= t, nearT = abs(p.y - r.y) <= t, nearB = abs(p.y - r.bottom) <= t
        let inX = p.x >= r.x - t && p.x <= r.right + t, inY = p.y >= r.y - t && p.y <= r.bottom + t
        if nearL && nearT { return .topLeft }
        if nearR && nearT { return .topRight }
        if nearR && nearB { return .bottomRight }
        if nearL && nearB { return .bottomLeft }
        if nearT && inX { return .top }
        if nearB && inX { return .bottom }
        if nearL && inY { return .left }
        if nearR && inY { return .right }
        return r.contains(p) ? .move : .none
    }

    public func beginDrag(_ h: Handle, at p: PixelPoint) { dragHandle = h; dragStart = p; dragOrigin = rect }

    public func drag(to p: PixelPoint, snap: EdgeSnap? = nil) {
        let dx = p.x - dragStart.x, dy = p.y - dragStart.y
        let o = dragOrigin
        var x1 = o.x, y1 = o.y, x2 = o.right, y2 = o.bottom
        switch dragHandle {
        case .move: rect = o; nudge(dx, dy); return
        case .topLeft: x1 += dx; y1 += dy
        case .top: y1 += dy
        case .topRight: x2 += dx; y1 += dy
        case .right: x2 += dx
        case .bottomRight: x2 += dx; y2 += dy
        case .bottom: y2 += dy
        case .bottomLeft: x1 += dx; y2 += dy
        case .left: x1 += dx
        case .none: return
        }
        let h = dragHandle
        let mx1 = [.topLeft, .left, .bottomLeft].contains(h), mx2 = [.topRight, .right, .bottomRight].contains(h)
        let my1 = [.topLeft, .top, .topRight].contains(h), my2 = [.bottomLeft, .bottom, .bottomRight].contains(h)
        func edge(_ e: Int, _ c: Int, _ lo: Int, _ hi: Int) -> Int { c <= lo ? lo : c >= hi - 1 ? hi : e }
        if mx1 { x1 = edge(x1, p.x, bounds.x, bounds.right) }
        if mx2 { x2 = edge(x2, p.x, bounds.x, bounds.right) }
        if my1 { y1 = edge(y1, p.y, bounds.y, bounds.bottom) }
        if my2 { y2 = edge(y2, p.y, bounds.y, bounds.bottom) }
        if let snap {
            func inside(_ axis: EdgeAxis, _ e: Int, _ cross: Int, _ lo: Int, _ hi: Int) -> Int {
                if e <= lo || e >= hi { return e }
                if let s = snap(axis, e, cross), s >= lo, s <= hi { return s }
                return e
            }
            let sx1 = mx1 ? inside(.vertical, x1, p.y, bounds.x, bounds.right) : x1
            let sx2 = mx2 ? inside(.vertical, x2, p.y, bounds.x, bounds.right) : x2
            let sy1 = my1 ? inside(.horizontal, y1, p.x, bounds.y, bounds.bottom) : y1
            let sy2 = my2 ? inside(.horizontal, y2, p.x, bounds.y, bounds.bottom) : y2
            if (sx2 - sx1).signum() == (x2 - x1).signum() && sx2 != sx1 { (x1, x2) = (sx1, sx2) }
            if (sy2 - sy1).signum() == (y2 - y1).signum() && sy2 != sy1 { (y1, y2) = (sy1, sy2) }
        }
        func axis(_ a: Int, _ b: Int, _ lo: Int, _ hi: Int) -> (Int, Int) {
            let l = clamp(min(a, b), lo, hi), u = clamp(max(a, b), lo, hi)
            return u > l ? (l, u - l) : (clamp(l, lo, hi - 1), 1)
        }
        let (x, w) = axis(x1, x2, bounds.x, bounds.right), (y, hh) = axis(y1, y2, bounds.y, bounds.bottom)
        rect = PixelRect(x: x, y: y, width: w, height: hh)
    }

    public func endDrag() { dragHandle = .none }
}

// MARK: - HUD placement (Overlay/HudLayout.cs), y-down points

public enum HudLayout {
    public static let gap = 8.0, flyoutGap = 6.0

    public static func place(selection s: CGRect, work: CGRect, toolbar: CGSize) -> (CGPoint, below: Bool) {
        let candidates: [(Double, Bool)] = [(s.maxY + gap, true), (s.minY - gap - toolbar.height, false), (s.maxY - gap - toolbar.height, false)]
        var chosen = candidates.last!
        for c in candidates where c.0 >= work.minY + gap && c.0 + toolbar.height <= work.maxY - gap { chosen = c; break }
        let y = max(work.minY + gap, min(chosen.0, work.maxY - gap - toolbar.height))
        let x = clampInto(s.minX, work.minX + gap, work.maxX - toolbar.width - gap)
        return (CGPoint(x: x, y: y), chosen.1)
    }

    public static func placeFlyout(anchor: CGRect, size: CGSize, work: CGRect, preferBelow: Bool, avoid: CGRect? = nil) -> (CGPoint, below: Bool) {
        let w = size.width, h = size.height
        let x = clampInto(anchor.midX - w / 2, work.minX + gap, work.maxX - gap - w)
        func y(_ below: Bool) -> Double { below ? anchor.maxY + flyoutGap : anchor.minY - flyoutGap - h }
        func inWork(_ below: Bool) -> Bool { y(below) >= work.minY + gap && y(below) + h <= work.maxY - gap }
        func clear(_ below: Bool) -> Bool { avoid.map { !CGRect(x: x, y: y(below), width: w, height: h).intersects($0) } ?? true }
        for b in [preferBelow, !preferBelow] where inWork(b) && clear(b) { return (CGPoint(x: x, y: y(b)), b) }
        for b in [preferBelow, !preferBelow] where inWork(b) { return (CGPoint(x: x, y: y(b)), b) }
        let roomier = work.maxY - anchor.maxY >= anchor.minY - work.minY
        return (CGPoint(x: x, y: max(work.minY + gap, min(y(roomier), work.maxY - gap - h))), roomier)
    }
}

public struct RulerMeasurement: Equatable {
    public var start, end: CGPoint
    public var dx: Double { end.x - start.x }
    public var dy: Double { end.y - start.y }
    public var length: Double { (end - start).length }
    public var label: String {
        let len = (length * 10).rounded() / 10
        let lenText = len == len.rounded() ? String(format: "%.0f", len) : String(format: "%.1f", len)
        return "Δx \(Int(abs(dx.rounded()))) · Δy \(Int(abs(dy.rounded()))) · \(lenText) px"
    }

    public static func snap45(_ s: CGPoint, _ e: CGPoint) -> CGPoint {
        let v = e - s
        if v.lengthSquared < 1e-12 { return e }
        let a = (atan2(v.dy, v.dx) / (.pi / 4)).rounded() * (.pi / 4)
        let p = s + CGVector(dx: cos(a), dy: sin(a)) * v.length
        return CGPoint(x: (p.x * 1e6).rounded() / 1e6, y: (p.y * 1e6).rounded() / 1e6)
    }

    public static func between(_ s: CGPoint, _ e: CGPoint, snap: Bool) -> RulerMeasurement {
        let a = CGPoint(x: s.x.rounded(), y: s.y.rounded()), b = CGPoint(x: e.x.rounded(), y: e.y.rounded())
        return RulerMeasurement(start: a, end: snap ? snap45(a, b) : b)
    }
}

// MARK: - Selection edge snapping to sharp image edges (Overlay/EdgeSnapper.cs)

public final class EdgeSnapper {
    public static let bandHalf = 20, minStep = 8, crossStep = 4
    public static let minContrast = 16.0, minAgreement = 0.8
    private let image: PixelImage
    private var scores: [Int: Double] = [:]

    public init(_ image: PixelImage) { self.image = image }
    public func resetCache() { scores.removeAll() }

    func luma(_ x: Int, _ y: Int) -> Int {
        let i = (y * image.width + x) * 4
        return (Int(image.pixels[i]) * 299 + Int(image.pixels[i + 1]) * 587 + Int(image.pixels[i + 2]) * 114) / 1000
    }

    public func find(_ axis: EdgeAxis, edge: Int, cross c: Int, threshold: Int) -> Int? {
        let length = axis == .vertical ? image.width : image.height
        let crossLength = axis == .vertical ? image.height : image.width
        if threshold < 0 || length < 2 || crossLength < 1 { return nil }
        let cross = clamp(c / Self.crossStep * Self.crossStep + Self.crossStep / 2, 0, crossLength - 1)
        let from = max(1, edge - threshold), to = min(length - 1, edge + threshold)
        guard from <= to else { return nil }
        var best: Int?
        for e in from...to {
            let s = score(axis, e, cross)
            if s <= 0 || s < score(axis, e - 1, cross) || s <= score(axis, e + 1, cross) { continue }
            if best == nil || abs(e - edge) < abs(best! - edge) { best = e }
        }
        return best
    }

    func score(_ axis: EdgeAxis, _ e: Int, _ cross: Int) -> Double {
        let length = axis == .vertical ? image.width : image.height
        if e < 1 || e > length - 1 { return 0 }
        let key = (axis == .vertical ? 1 : 0) | (e << 1) | (cross << 22)
        if let s = scores[key] { return s }
        let crossLength = axis == .vertical ? image.height : image.width
        let c0 = max(0, cross - Self.bandHalf), c1 = min(crossLength - 1, cross + Self.bandHalf)
        let n = c1 - c0 + 1
        var sum = 0, up = 0, down = 0
        for c in c0...c1 {
            let d = axis == .vertical ? luma(e, c) - luma(e - 1, c) : luma(c, e) - luma(c, e - 1)
            sum += d
            if d >= Self.minStep { up += 1 } else if d <= -Self.minStep { down += 1 }
        }
        let mean = Double(sum) / Double(n)
        let agree = mean >= 0 ? up : down
        let s = abs(mean) >= Self.minContrast && Double(agree) >= Self.minAgreement * Double(n) ? abs(mean) : 0
        scores[key] = s
        return s
    }
}

/// Windows under the cursor (front to back), in screenshot pixels.
public struct WindowInfo: Equatable {
    public var rect: PixelRect
    public init(rect: PixelRect) { self.rect = rect }

    public static func topmost(_ windows: [WindowInfo], at p: PixelPoint) -> WindowInfo? { windows.first { $0.rect.contains(p) } }

    public static func visibleEdges(_ windows: [WindowInfo], _ axis: EdgeAxis, _ cross: Int) -> [Int] {
        var out: [Int] = []
        for w in windows {
            let r = w.rect
            if axis == .vertical {
                if cross < r.y || cross >= r.bottom { continue }
                if topmost(windows, at: PixelPoint(x: r.x, y: cross)) == w { out.append(r.x) }
                if topmost(windows, at: PixelPoint(x: r.right - 1, y: cross)) == w { out.append(r.right) }
            } else {
                if cross < r.x || cross >= r.right { continue }
                if topmost(windows, at: PixelPoint(x: cross, y: r.y)) == w { out.append(r.y) }
                if topmost(windows, at: PixelPoint(x: cross, y: r.bottom - 1)) == w { out.append(r.bottom) }
            }
        }
        return out
    }
}

/// Composes the final picture: region pixels + annotations, then the beautify frame if on.
public enum ImageComposer {
    public static func compose(_ shot: PixelImage, region: PixelRect, doc: AnnotationDocument?) -> CGImage {
        let inside = region.clamped(to: shot.bounds)
        if inside == region && (doc?.items.isEmpty ?? true) { return shot.cropped(region).cgImage }
        let ctx = CGContext(data: nil, width: region.width, height: region.height, bitsPerComponent: 8, bytesPerRow: 0,
                            space: PixelImage.colorSpace, bitmapInfo: PixelImage.bitmapInfo)!
        ctx.translateBy(x: 0, y: CGFloat(region.height)); ctx.scaleBy(x: 1, y: -1) // y-down
        if !inside.isEmpty {
            let img = shot.cropped(inside).cgImage
            let r = CGRect(x: inside.x - region.x, y: inside.y - region.y, width: inside.width, height: inside.height)
            ctx.saveGState()
            ctx.translateBy(x: r.minX, y: r.maxY); ctx.scaleBy(x: 1, y: -1)
            ctx.draw(img, in: CGRect(origin: .zero, size: r.size))
            ctx.restoreGState()
        }
        ctx.translateBy(x: CGFloat(-region.x), y: CGFloat(-region.y))
        doc?.render(ctx, RenderContext(source: shot, forExport: true))
        return ctx.makeImage()!
    }
}

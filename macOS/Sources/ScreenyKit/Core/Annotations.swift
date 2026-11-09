import AppKit
import CoreGraphics

/// What a render pass needs besides the context: effects read pixels from the original screenshot.
public struct RenderContext {
    public let source: PixelImage
    public let forExport: Bool
    public init(source: PixelImage, forExport: Bool) { self.source = source; self.forExport = forExport }
}

/// Vector annotation in screenshot pixels (Annotations/Annotation.cs). The same render is used on screen and in export.
/// Drawing contexts are y-down (flipped), one unit = one screenshot pixel.
public class Annotation {
    public var style: AnnotationStyle { didSet { if style != oldValue { styleChanged() } } }
    init(style: AnnotationStyle) { self.style = style }

    /// Every pixel the render touches (stroke, heads, background).
    public var bounds: CGRect { .null }
    /// Geometry without stroke width: snapping uses its edges and center.
    public var geometryBounds: CGRect { bounds }
    public func render(_ ctx: CGContext, _ rc: RenderContext) {}
    public func hitTest(_ p: CGPoint) -> Bool { bounds.inflated(4).contains(p) }
    public func offset(_ d: CGVector) {}
    func styleChanged() {}

    func strokeContains(_ path: CGPath, width: CGFloat, _ p: CGPoint, cap: CGLineCap = .round) -> Bool {
        path.copy(strokingWithWidth: width, lineCap: cap, lineJoin: .round, miterLimit: 10).contains(p)
    }

    static func setStroke(_ ctx: CGContext, _ color: RGBA, _ width: Double, cap: CGLineCap = .round) {
        ctx.setStrokeColor(color.cgColor)
        ctx.setLineWidth(width)
        ctx.setLineCap(cap)
        ctx.setLineJoin(.round)
    }
}

// MARK: - Lines and arrows

public class LineAnnotation: Annotation {
    public var start: CGPoint { didSet { geometryChanged() } }
    public var end: CGPoint { didSet { geometryChanged() } }

    public init(_ start: CGPoint, _ end: CGPoint, style: AnnotationStyle) {
        self.start = start; self.end = end
        super.init(style: style)
    }

    func geometryChanged() {}

    public override var bounds: CGRect { CGRect(corner: start, end).inflated(style.thickness / 2) }
    public override var geometryBounds: CGRect { CGRect(corner: start, end) }

    public override func render(_ ctx: CGContext, _ rc: RenderContext) {
        Annotation.setStroke(ctx, style.color, style.thickness)
        ctx.strokeLineSegments(between: [start, end])
    }

    public override func hitTest(_ p: CGPoint) -> Bool {
        let w = style.thickness + 8
        guard CGRect(corner: start, end).inflated(w / 2).contains(p) else { return false }
        return ArrowGeometry.distanceToSegment(p, start, end) <= w / 2
    }

    public override func offset(_ d: CGVector) { start += d; end += d }
}

public struct ArrowShape {
    public var hasLine: Bool
    public var lineStart: CGPoint
    public var lineEnd: CGPoint
    public var dashed: Bool
    public var polygons: [[CGPoint]]
    public static let empty = ArrowShape(hasLine: false, lineStart: .zero, lineEnd: .zero, dashed: false, polygons: [])
}

/// Arrow geometry without rendering (Annotations/Shapes/ArrowGeometry.cs).
public enum ArrowGeometry {
    public static func headLength(_ t: Double) -> Double { max(12, t * 4) }

    public static func compute(_ start: CGPoint, _ end: CGPoint, thickness: Double, style: ArrowStyle) -> ArrowShape {
        var dir = end - start
        let length = dir.length
        if length < 1e-6 { return .empty }
        dir = dir / length
        switch style {
        case .double:
            let head = min(headLength(thickness), length * 0.4)
            return ArrowShape(hasLine: true, lineStart: start + dir * (head * 0.9), lineEnd: end - dir * (head * 0.9), dashed: false,
                              polygons: [Self.head(end, dir, head), Self.head(start, -dir, head)])
        case .thick:
            let head = min(max(16, thickness * 5), length * 0.5)
            let body = max(3, thickness * 1.2), wing = max(body * 2.2, head * 0.55), tail = max(0.5, thickness * 0.2)
            let n = CGVector(dx: -dir.dy, dy: dir.dx)
            let neck = end - dir * head
            return ArrowShape(hasLine: false, lineStart: start, lineEnd: end, dashed: false, polygons: [[
                start + n * tail, neck + n * body, neck + n * wing, end, neck - n * wing, neck - n * body, start - n * tail,
            ]])
        case .normal, .dashed:
            let head = min(headLength(thickness), length * 0.6)
            return ArrowShape(hasLine: true, lineStart: start, lineEnd: end - dir * (head * 0.9), dashed: style == .dashed,
                              polygons: [Self.head(end, dir, head)])
        }
    }

    static func head(_ tip: CGPoint, _ dir: CGVector, _ head: Double) -> [CGPoint] {
        let base = tip - dir * head
        let normal = CGVector(dx: -dir.dy, dy: dir.dx) * (head * 0.8 / 2)
        return [tip, base - normal, base + normal]
    }

    public static func bounds(_ s: ArrowShape, thickness: Double) -> CGRect {
        var r = CGRect.null
        if s.hasLine { r = r.union(point: s.lineStart).union(point: s.lineEnd) }
        for poly in s.polygons { for p in poly { r = r.union(point: p) } }
        return r.isNull ? r : r.insetBy(dx: -(thickness / 2 + 1), dy: -(thickness / 2 + 1))
    }

    public static func hitTest(_ s: ArrowShape, _ p: CGPoint, tolerance: Double) -> Bool {
        if s.hasLine && distanceToSegment(p, s.lineStart, s.lineEnd) <= tolerance { return true }
        for poly in s.polygons where contains(poly, p) || distanceToPolygon(p, poly) <= min(tolerance, 4) { return true }
        return false
    }

    public static func distanceToSegment(_ p: CGPoint, _ a: CGPoint, _ b: CGPoint) -> Double {
        let ab = b - a
        let len2 = ab.lengthSquared
        if len2 < 1e-12 { return (p - a).length }
        let t = clamp(((p.x - a.x) * ab.dx + (p.y - a.y) * ab.dy) / len2, 0, 1)
        return (p - (a + ab * t)).length
    }

    static func distanceToPolygon(_ p: CGPoint, _ poly: [CGPoint]) -> Double {
        var best = Double.greatestFiniteMagnitude
        for i in poly.indices { best = min(best, distanceToSegment(p, poly[i], poly[(i + 1) % poly.count])) }
        return best
    }

    public static func contains(_ poly: [CGPoint], _ p: CGPoint) -> Bool {
        var inside = false
        var j = poly.count - 1
        for i in poly.indices {
            if (poly[i].y > p.y) != (poly[j].y > p.y),
               p.x < (poly[j].x - poly[i].x) * (p.y - poly[i].y) / (poly[j].y - poly[i].y) + poly[i].x { inside.toggle() }
            j = i
        }
        return inside
    }
}

public final class ArrowAnnotation: LineAnnotation {
    private var cached: ArrowShape?
    public var shape: ArrowShape {
        if let c = cached { return c }
        let s = ArrowGeometry.compute(start, end, thickness: style.thickness, style: style.arrow)
        cached = s
        return s
    }
    override func geometryChanged() { cached = nil }
    override func styleChanged() { cached = nil }

    public override var bounds: CGRect { shape.polygons.isEmpty ? super.bounds : ArrowGeometry.bounds(shape, thickness: style.thickness) }
    public override var geometryBounds: CGRect { bounds }
    public override func hitTest(_ p: CGPoint) -> Bool { ArrowGeometry.hitTest(shape, p, tolerance: style.thickness / 2 + 4) }

    public override func render(_ ctx: CGContext, _ rc: RenderContext) {
        let s = shape
        if s.polygons.isEmpty { return }
        if s.hasLine {
            ctx.saveGState()
            Annotation.setStroke(ctx, style.color, style.thickness)
            if s.dashed { ctx.setLineDash(phase: 0, lengths: [3 * style.thickness, 2.2 * style.thickness]) }
            ctx.strokeLineSegments(between: [s.lineStart, s.lineEnd])
            ctx.restoreGState()
        }
        let path = CGMutablePath()
        for poly in s.polygons { path.addLines(between: poly); path.closeSubpath() }
        ctx.setFillColor(style.color.cgColor)
        ctx.setStrokeColor(style.color.cgColor)
        ctx.setLineWidth(1) // a thin edge rounds the head corners
        ctx.setLineJoin(.round)
        ctx.addPath(path)
        ctx.drawPath(using: .fillStroke)
    }
}

// MARK: - Pen and highlighter

public class PenAnnotation: Annotation {
    public private(set) var points: [CGPoint]
    private var pointBounds: CGRect
    private var path = CGMutablePath()

    public init(_ start: CGPoint, style: AnnotationStyle) {
        points = [start]
        pointBounds = CGRect(origin: start, size: .zero)
        super.init(style: style)
        rebuild()
    }

    public func addPoint(_ p: CGPoint) {
        points.append(p)
        pointBounds = pointBounds.union(point: p)
        if points.count == 2 { rebuild() } else { path.addLine(to: p) }
    }

    private func rebuild() {
        path = CGMutablePath()
        path.move(to: points[0])
        // A single point draws a micro-segment so a click leaves a dot.
        if points.count == 1 { path.addLine(to: points[0] + CGVector(dx: 0.01, dy: 0)) }
        for p in points.dropFirst() { path.addLine(to: p) }
    }

    var strokeWidth: Double { style.thickness }
    var strokeColor: RGBA { style.color }
    var cap: CGLineCap { .round }

    public override var bounds: CGRect {
        let pad = strokeWidth / 2 * (cap == .square ? 2.0.squareRoot() : 1)
        return pointBounds.insetBy(dx: -pad, dy: -pad)
    }

    public override func render(_ ctx: CGContext, _ rc: RenderContext) {
        ctx.saveGState()
        Annotation.setStroke(ctx, strokeColor, strokeWidth, cap: cap)
        ctx.addPath(path)
        ctx.strokePath()
        ctx.restoreGState()
    }

    public override func hitTest(_ p: CGPoint) -> Bool {
        let w = strokeWidth + 8
        return pointBounds.insetBy(dx: -w / 2, dy: -w / 2).contains(p) && strokeContains(path, width: w, p)
    }

    public override func offset(_ d: CGVector) {
        points = points.map { $0 + d }
        pointBounds = pointBounds.offset(d)
        rebuild()
    }
}

/// Semi-transparent wide line with square ends.
public final class HighlighterAnnotation: PenAnnotation {
    override var strokeWidth: Double { style.thickness * 4 }
    override var strokeColor: RGBA { RGBA(style.color.r, style.color.g, style.color.b, a: 0x66) }
    override var cap: CGLineCap { .square }
}

// MARK: - Boxes

public class BoxAnnotation: Annotation {
    public var rect: CGRect { didSet { geometryChanged() } }
    init(_ rect: CGRect, style: AnnotationStyle) { self.rect = rect; super.init(style: style) }
    func geometryChanged() {}
    public override var bounds: CGRect { rect }
    public override var geometryBounds: CGRect { rect }
    public override func offset(_ d: CGVector) { rect = rect.offset(d) }
}

public final class RectAnnotation: BoxAnnotation {
    public override init(_ rect: CGRect, style: AnnotationStyle) { super.init(rect, style: style) }
    public override var bounds: CGRect { style.strokeEnabled ? rect.inflated(style.thickness / 2) : rect }

    public override func render(_ ctx: CGContext, _ rc: RenderContext) {
        if let f = style.fill { ctx.setFillColor(f.cgColor); ctx.fill(rect) }
        if style.strokeEnabled {
            ctx.setStrokeColor(style.color.cgColor)
            ctx.setLineWidth(style.thickness)
            ctx.setLineJoin(.miter)
            ctx.stroke(rect)
        }
    }

    /// Without fill only the frame band is hit; a click inside passes through.
    public override func hitTest(_ p: CGPoint) -> Bool {
        let m = (style.strokeEnabled ? style.thickness / 2 : 0) + 4
        guard rect.inflated(m).contains(p) else { return false }
        if style.fill != nil { return true }
        let interior = rect.inflated(-m)
        return interior.isNull || !interior.contains(p)
    }
}

public final class EllipseAnnotation: BoxAnnotation {
    public override init(_ rect: CGRect, style: AnnotationStyle) { super.init(rect, style: style) }
    public override var bounds: CGRect { style.strokeEnabled ? rect.inflated(style.thickness / 2) : rect }

    public override func render(_ ctx: CGContext, _ rc: RenderContext) {
        if let f = style.fill { ctx.setFillColor(f.cgColor); ctx.fillEllipse(in: rect) }
        if style.strokeEnabled {
            ctx.setStrokeColor(style.color.cgColor)
            ctx.setLineWidth(style.thickness)
            ctx.strokeEllipse(in: rect)
        }
    }

    public override func hitTest(_ p: CGPoint) -> Bool {
        if rect.isEmpty && rect.width == 0 && rect.height == 0 { return false }
        let w = style.thickness + 8
        guard rect.inflated(w / 2).contains(p) else { return false }
        let path = CGPath(ellipseIn: rect, transform: nil)
        return (style.fill != nil && path.contains(p)) || strokeContains(path, width: w, p)
    }
}

/// Pixel effect over the screenshot (pixelate, blur). While dragged, a quarter-size preview is drawn.
public class EffectAnnotation: BoxAnnotation {
    public static let previewFactor = 4
    public private(set) var isLive = false
    private var full: (key: String, image: CGImage)?
    private var preview: (key: String, image: CGImage)?

    public func beginLiveEdit() { isLive = true }
    public func endLiveEdit() { isLive = false; preview = nil }

    func apply(_ px: inout [UInt8], _ w: Int, _ h: Int, scale: Double) {}

    public override func hitTest(_ p: CGPoint) -> Bool { rect.inflated(2).contains(p) }

    func pixelArea(_ src: PixelImage) -> PixelRect {
        if rect.isNull { return .empty }
        let x1 = max(0, Int(floor(rect.minX))), y1 = max(0, Int(floor(rect.minY)))
        let x2 = min(src.width, Int(ceil(rect.maxX))), y2 = min(src.height, Int(ceil(rect.maxY)))
        return x2 - x1 < 1 || y2 - y1 < 1 ? .empty : .fromCorners(x1, y1, x2, y2)
    }

    private func key(_ a: PixelRect, _ src: PixelImage) -> String { "\(a)|\(style.thickness)|\(ObjectIdentifier(src).hashValue)" }

    public func process(_ src: PixelImage) -> CGImage? {
        let area = pixelArea(src)
        if area.isEmpty { return nil }
        let k = key(area, src)
        if let f = full, f.key == k { return f.image }
        var px = src.copyPixels(area)
        apply(&px, area.width, area.height, scale: 1)
        let img = PixelImage(pixels: px, width: area.width, height: area.height).cgImage
        full = (k, img)
        return img
    }

    func processPreview(_ src: PixelImage) -> CGImage? {
        let area = pixelArea(src)
        if area.isEmpty { return nil }
        let f = Self.previewFactor
        if area.width < f * 2 || area.height < f * 2 { return process(src) }
        let k = key(area, src)
        if let p = preview, p.key == k { return p.image }
        let pw = area.width / f, ph = area.height / f
        var px = [UInt8](repeating: 0, count: pw * ph * 4)
        for y in 0..<ph {
            for x in 0..<pw {
                let s = ((area.y + y * f) * src.width + area.x + x * f) * 4
                let d = (y * pw + x) * 4
                px[d] = src.pixels[s]; px[d + 1] = src.pixels[s + 1]; px[d + 2] = src.pixels[s + 2]; px[d + 3] = src.pixels[s + 3]
            }
        }
        apply(&px, pw, ph, scale: 1.0 / Double(f))
        let img = PixelImage(pixels: px, width: pw, height: ph).cgImage
        preview = (k, img)
        return img
    }

    public override func render(_ ctx: CGContext, _ rc: RenderContext) {
        let area = pixelArea(rc.source)
        if area.isEmpty { return }
        guard let img = isLive && !rc.forExport ? processPreview(rc.source) : process(rc.source) else { return }
        ctx.saveGState()
        ctx.interpolationQuality = isLive && !rc.forExport ? .low : .none
        // The context is flipped: draw the image upright.
        let r = area.cgRect
        ctx.translateBy(x: r.minX, y: r.maxY)
        ctx.scaleBy(x: 1, y: -1)
        ctx.draw(img, in: CGRect(x: 0, y: 0, width: r.width, height: r.height))
        ctx.restoreGState()
    }
}

public final class PixelateAnnotation: EffectAnnotation {
    public override init(_ rect: CGRect, style: AnnotationStyle) { super.init(rect, style: style) }
    override func apply(_ px: inout [UInt8], _ w: Int, _ h: Int, scale: Double) {
        PixelOps.pixelate(&px, width: w, height: h, blockSize: max(1, Int((Double(Int(max(6, style.thickness * 3))) * scale).rounded())))
    }
}

public final class BlurAnnotation: EffectAnnotation {
    public override init(_ rect: CGRect, style: AnnotationStyle) { super.init(rect, style: style) }
    override func apply(_ px: inout [UInt8], _ w: Int, _ h: Int, scale: Double) {
        let r = max(1, Int((Double(Int(max(3, style.thickness * 2))) * scale).rounded()))
        // Two box passes ≈ Gaussian: text underneath is unreadable.
        PixelOps.boxBlur(&px, width: w, height: h, radius: r)
        PixelOps.boxBlur(&px, width: w, height: h, radius: r)
    }
}

// MARK: - Text

public final class TextAnnotation: Annotation {
    public static let backgroundPadX = 8.0, backgroundPadY = 4.0, backgroundRadius = 6.0
    public private(set) var origin: CGPoint
    public var text: String { didSet { layoutCache = nil } }
    private var layoutCache: (string: NSAttributedString, size: CGSize)?

    public init(_ origin: CGPoint, text: String, style: AnnotationStyle) {
        self.origin = origin; self.text = text
        super.init(style: style)
    }

    public static func fontSize(_ thickness: Double) -> Double { max(14, thickness * 6) }
    public static func font(_ thickness: Double) -> NSFont { NSFont.systemFont(ofSize: fontSize(thickness), weight: .semibold) }

    override func styleChanged() { layoutCache = nil }

    static func attributed(_ text: String, style: AnnotationStyle, color: RGBA) -> NSAttributedString {
        let p = NSMutableParagraphStyle()
        p.alignment = style.textAlign == .center ? .center : style.textAlign == .right ? .right : .left
        return NSAttributedString(string: text, attributes: [
            .font: font(style.thickness), .foregroundColor: color.nsColor, .paragraphStyle: p,
        ])
    }

    private var layout: (string: NSAttributedString, size: CGSize) {
        if let l = layoutCache { return l }
        let s = Self.attributed(text, style: style, color: style.color)
        let r = s.boundingRect(with: CGSize(width: 100_000, height: 100_000), options: [.usesLineFragmentOrigin])
        let font = Self.font(style.thickness)
        let lines = max(1, text.components(separatedBy: "\n").count)
        let height = max(ceil(r.height), ceil(font.ascender - font.descender + font.leading) * CGFloat(lines))
        let l = (s, CGSize(width: max(1, ceil(r.width)), height: height))
        layoutCache = l
        return l
    }

    public var textBounds: CGRect { CGRect(origin: origin, size: layout.size) }
    public static func backgroundRect(_ text: CGRect) -> CGRect { text.insetBy(dx: -backgroundPadX, dy: -backgroundPadY) }
    public override var bounds: CGRect { style.textBackground == nil ? textBounds : Self.backgroundRect(textBounds) }

    public override func render(_ ctx: CGContext, _ rc: RenderContext) {
        let l = layout
        let box = CGRect(origin: origin, size: l.size)
        if let bg = style.textBackground {
            ctx.setFillColor(bg.cgColor)
            let r = Self.backgroundRect(box)
            ctx.addPath(CGPath(roundedRect: r, cornerWidth: Self.backgroundRadius, cornerHeight: Self.backgroundRadius, transform: nil))
            ctx.fillPath()
        }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: true)
        if style.textBackground == nil {
            // A 1 px shadow keeps text readable on any background.
            let shadow = Self.attributed(text, style: style, color: RGBA(0, 0, 0, a: 0x80))
            shadow.draw(with: box.offsetBy(dx: 1, dy: 1), options: [.usesLineFragmentOrigin])
        }
        l.string.draw(with: box, options: [.usesLineFragmentOrigin])
        NSGraphicsContext.restoreGraphicsState()
    }

    public override func offset(_ d: CGVector) { origin += d }
}

/// Numbered badge: filled circle in the style color with a white number.
public final class NumberAnnotation: Annotation {
    public private(set) var center: CGPoint
    public let number: Int

    public init(_ center: CGPoint, number: Int, style: AnnotationStyle) {
        self.center = center; self.number = number
        super.init(style: style)
    }

    var radius: Double { max(11, style.thickness * 4) }
    var outlineWidth: Double { max(1.5, radius / 8) }

    public override var geometryBounds: CGRect { CGRect(x: center.x - radius, y: center.y - radius, width: radius * 2, height: radius * 2) }
    public override var bounds: CGRect { geometryBounds.inflated(outlineWidth / 2) }
    public override func hitTest(_ p: CGPoint) -> Bool { (p - center).length <= radius + 2 }
    public override func offset(_ d: CGVector) { center += d }

    public override func render(_ ctx: CGContext, _ rc: RenderContext) {
        ctx.setFillColor(style.color.cgColor)
        ctx.setStrokeColor(RGBA(255, 255, 255, a: 0xCC).cgColor)
        ctx.setLineWidth(outlineWidth)
        ctx.addEllipse(in: geometryBounds)
        ctx.drawPath(using: .fillStroke)
        let size = radius * (number >= 10 ? 0.95 : 1.15)
        let s = NSAttributedString(string: String(number), attributes: [
            .font: NSFont.systemFont(ofSize: size, weight: .bold), .foregroundColor: NSColor.white,
        ])
        let sz = s.size()
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: true)
        s.draw(at: CGPoint(x: center.x - sz.width / 2, y: center.y - sz.height / 2))
        NSGraphicsContext.restoreGraphicsState()
    }
}

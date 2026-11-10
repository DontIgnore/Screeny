import AppKit

/// The frozen screenshot as a layer (cheap to redraw over: only the canvas above it repaints).
final class ShotView: NSView {
    private let image: CGImage
    /// Part of the image this view shows, in unit coordinates with the origin at the bottom left (CALayer contentsRect).
    private let crop: CGRect
    init(image: CGImage, crop: CGRect = CGRect(x: 0, y: 0, width: 1, height: 1)) {
        self.image = image
        self.crop = crop
        super.init(frame: .zero)
        wantsLayer = true
        layer?.contents = image
        layer?.contentsRect = crop
        layer?.contentsGravity = .resize
        layer?.magnificationFilter = .nearest
        layer?.minificationFilter = .linear
    }
    required init?(coder: NSCoder) { fatalError() }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
    /// Used only by offscreen rendering (cacheDisplay); on screen the layer shows the image.
    override func draw(_ dirtyRect: NSRect) {
        let full = CGSize(width: bounds.width / crop.width, height: bounds.height / crop.height)
        NSGraphicsContext.current?.cgContext.draw(image, in: CGRect(x: -crop.minX * full.width, y: -crop.minY * full.height, width: full.width, height: full.height))
    }
}

/// Everything drawn in screenshot pixels: annotations, dim with the selection hole, frame and handles, window hint,
/// ruler, text-mode highlights and snap guides (DimLayer, AnnotationLayer, RulerLayer, TextLayer, SnapGuideLayer).
final class CanvasView: FlippedView {
    weak var model: CanvasModel?
    /// Where this view's top-left sits in the model's view (another display's part of the virtual screen).
    var offset = CGPoint.zero

    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func draw(_ dirtyRect: NSRect) {
        guard let m = model, let ctx = NSGraphicsContext.current?.cgContext else { return }
        let k = 1 / m.pixelScale // pixels → points
        ctx.saveGState()
        ctx.translateBy(x: -offset.x, y: -offset.y)
        ctx.scaleBy(x: k, y: k)
        let s = m.pixelScale // one point in pixels
        let full = CGRect(x: 0, y: 0, width: m.shot.width, height: m.shot.height)

        // Annotations, clipped to the selection (effects never spill out).
        ctx.saveGState()
        if let clip = m.annotationClip { ctx.clip(to: clip) }
        m.document.render(ctx, RenderContext(source: m.shot, forExport: false))
        ctx.restoreGState()

        // Dim around the selection.
        let dim = RGBA(0, 0, 0, a: m.dimAlpha).cgColor
        ctx.setFillColor(dim)
        if let hole = m.hole, !hole.isEmpty {
            let h = hole.cgRect.intersection(full)
            for r in [CGRect(x: 0, y: 0, width: full.width, height: h.minY),
                      CGRect(x: 0, y: h.maxY, width: full.width, height: full.maxY - h.maxY),
                      CGRect(x: 0, y: h.minY, width: h.minX, height: h.height),
                      CGRect(x: h.maxX, y: h.minY, width: full.maxX - h.maxX, height: h.height)] where r.width > 0 && r.height > 0 {
                ctx.fill(r)
            }
            if m.showFrame {
                // White line with a blue dash on top, 1 point wide, just outside the selection.
                let w = s
                let border = CGRect(x: CGFloat(hole.x) - w / 2, y: CGFloat(hole.y) - w / 2, width: CGFloat(hole.width) + w, height: CGFloat(hole.height) + w)
                ctx.setLineWidth(w)
                ctx.setStrokeColor(CGColor(gray: 1, alpha: 1)); ctx.stroke(border)
                ctx.setStrokeColor(Palette.accent.cgColor)
                ctx.setLineDash(phase: 0, lengths: [4 * s, 4 * s]); ctx.stroke(border); ctx.setLineDash(phase: 0, lengths: [])
            }
            if m.showHandles { drawHandles(ctx, hole, s) }
        } else {
            ctx.fill(full)
        }

        if let hint = m.hint, !hint.isEmpty {
            let r = hint.cgRect.insetBy(dx: s, dy: s)
            ctx.setFillColor(RGBA(0x4C, 0x9B, 0xFF, a: 0x1A).cgColor); ctx.fill(r)
            ctx.setStrokeColor(RGBA(hex: 0x4C9BFF).cgColor); ctx.setLineWidth(2 * s); ctx.stroke(r)
        }

        if let t = m.textLayer { drawText(ctx, t, s) }
        if let r = m.ruler { drawRuler(ctx, r, s) }
        drawGuides(ctx, m.guides, s)
        ctx.restoreGState()
    }

    private func drawHandles(_ ctx: CGContext, _ r: PixelRect, _ s: CGFloat) {
        let size = (7 * s).rounded(), h = size / 2
        let cx = CGFloat(r.x) + CGFloat(r.width) / 2, cy = CGFloat(r.y) + CGFloat(r.height) / 2
        let pts = [(CGFloat(r.x), CGFloat(r.y)), (cx, CGFloat(r.y)), (CGFloat(r.right), CGFloat(r.y)), (CGFloat(r.right), cy),
                   (CGFloat(r.right), CGFloat(r.bottom)), (cx, CGFloat(r.bottom)), (CGFloat(r.x), CGFloat(r.bottom)), (CGFloat(r.x), cy)]
        ctx.setLineWidth(s)
        for (x, y) in pts {
            let rr = CGRect(x: (x - h).rounded(), y: (y - h).rounded(), width: size, height: size)
            ctx.setFillColor(CGColor(gray: 1, alpha: 1)); ctx.fill(rr)
            ctx.setStrokeColor(Palette.accent.cgColor); ctx.stroke(rr.insetBy(dx: s / 2, dy: s / 2))
        }
    }

    private func drawGuides(_ ctx: CGContext, _ guides: [SnapGuide], _ s: CGFloat) {
        guard !guides.isEmpty else { return }
        // Accent line with a white halo (SnapGuideLayer.cs).
        for (w, c) in [(3 * s, RGBA(255, 255, 255, a: 0xB3)), (s, Palette.accent)] {
            ctx.setStrokeColor(c.cgColor)
            ctx.setLineWidth(w)
            for g in guides {
                if g.orientation == .vertical { ctx.strokeLineSegments(between: [CGPoint(x: g.position, y: g.start), CGPoint(x: g.position, y: g.end)]) }
                else { ctx.strokeLineSegments(between: [CGPoint(x: g.start, y: g.position), CGPoint(x: g.end, y: g.position)]) }
            }
        }
    }

    private func drawRuler(_ ctx: CGContext, _ m: RulerMeasurement, _ s: CGFloat) {
        let a = m.start + CGVector(dx: 0.5, dy: 0.5), b = m.end + CGVector(dx: 0.5, dy: 0.5)
        let dir = b - a
        let n = dir.lengthSquared > 1e-9 ? CGVector(dx: -dir.dy, dy: dir.dx) / dir.length * (6 * s) : CGVector(dx: 0, dy: 6 * s)
        ctx.setLineCap(.round)
        for (w, c) in [(4 * s, RGBA(255, 255, 255, a: 0xCC)), (2 * s, Palette.accent)] {
            ctx.setLineWidth(w); ctx.setStrokeColor(c.cgColor)
            ctx.strokeLineSegments(between: [a, b, a - n, a + n, b - n, b + n])
        }
        if m.length < 1 { return }
        let text = NSAttributedString(string: m.label, attributes: [.font: NSFont.systemFont(ofSize: 12 * s, weight: .semibold), .foregroundColor: NSColor.white])
        let ts = text.size()
        let size = CGSize(width: ts.width + 16 * s, height: ts.height + 8 * s)
        let mid = a + dir / 2
        var off = dir.lengthSquared > 1e-9 ? CGVector(dx: -dir.dy, dy: dir.dx) / dir.length : CGVector(dx: 0, dy: -1)
        if off.dy > 0 { off = -off }
        let clearance = abs(off.dx) * size.width / 2 + abs(off.dy) * size.height / 2 + 6 * s
        let c = mid + off * clearance
        let box = CGRect(x: c.x - size.width / 2, y: c.y - size.height / 2, width: size.width, height: size.height)
        ctx.setFillColor(RGBA(0x29, 0x25, 0x24, a: 0xF2).cgColor)
        ctx.addPath(CGPath(roundedRect: box, cornerWidth: 6 * s, cornerHeight: 6 * s, transform: nil)); ctx.fillPath()
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: true)
        text.draw(at: CGPoint(x: box.minX + 8 * s, y: box.minY + 4 * s))
        NSGraphicsContext.restoreGraphicsState()
    }

    private func drawText(_ ctx: CGContext, _ t: TextLayerState, _ s: CGFloat) {
        let pad = 2 * s, radius = 3 * s
        // Shade the area, cutting a light window under each word and code.
        ctx.saveGState()
        let path = CGMutablePath()
        path.addRect(t.area)
        for i in 0..<t.layout.wordCount {
            let b = t.layout.word(i).box.insetBy(dx: -pad, dy: -pad)
            path.addPath(CGPath(roundedRect: b, cornerWidth: radius, cornerHeight: radius, transform: nil))
        }
        for c in t.codes { path.addPath(CGPath(roundedRect: c, cornerWidth: radius, cornerHeight: radius, transform: nil)) }
        ctx.addPath(path)
        ctx.setFillColor(RGBA(0, 0, 0, a: 0x4A).cgColor)
        ctx.fillPath(using: .evenOdd)
        ctx.restoreGState()
        ctx.setFillColor(RGBA(0x30, 0x78, 0xF6, a: 0x5C).cgColor)
        for run in t.layout.selectionRuns(t.selection) {
            ctx.addPath(CGPath(roundedRect: run.insetBy(dx: -pad, dy: -pad), cornerWidth: radius, cornerHeight: radius, transform: nil))
            ctx.fillPath()
        }
        ctx.setLineWidth(2 * s)
        ctx.setStrokeColor(Palette.accent.cgColor)
        for (i, c) in t.codes.enumerated() {
            let p = CGPath(roundedRect: c, cornerWidth: radius, cornerHeight: radius, transform: nil)
            if i == t.activeCode { ctx.setFillColor(RGBA(0x30, 0x78, 0xF6, a: 0x33).cgColor); ctx.addPath(p); ctx.fillPath() }
            ctx.addPath(p); ctx.strokePath()
        }
        if let m = t.marquee {
            ctx.setFillColor(RGBA(0x30, 0x78, 0xF6, a: 0x1A).cgColor); ctx.fill(m)
            ctx.setLineWidth(s); ctx.setLineDash(phase: 0, lengths: [4 * s, 3 * s]); ctx.stroke(m); ctx.setLineDash(phase: 0, lengths: [])
        }
    }
}

struct TextLayerState {
    var layout: OcrLayout
    var area: CGRect
    var selection: Set<Int> = []
    var marquee: CGRect?
    var codes: [CGRect] = []
    var activeCode: Int?
}

/// State the canvas draws from.
protocol CanvasModel: AnyObject {
    var shot: PixelImage { get }
    var pixelScale: CGFloat { get }
    var document: AnnotationDocument { get }
    var annotationClip: CGRect? { get }
    var hole: PixelRect? { get }
    var hint: PixelRect? { get }
    var showFrame: Bool { get }
    var showHandles: Bool { get }
    var dimAlpha: UInt8 { get }
    var ruler: RulerMeasurement? { get }
    var guides: [SnapGuide] { get }
    var textLayer: TextLayerState? { get }
}

/// Magnifier: 15×15 pixels around the cursor, coordinates and the pixel color (MagnifierControl.xaml).
final class MagnifierView: FlippedView {
    static let cells = 15, zoom: CGFloat = 124
    private var shot: PixelImage?
    private(set) var current: PixelPoint?
    var showColor = true { didSet { setFrameSize(CGSize(width: 132, height: showColor ? 162 : 147)) } }

    init() { super.init(frame: CGRect(x: 0, y: 0, width: 132, height: 162)) }
    required init?(coder: NSCoder) { fatalError() }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    func update(_ shot: PixelImage, _ p: PixelPoint) {
        if self.shot === shot && current == p { return }
        self.shot = shot; current = p
        needsDisplay = true
    }

    override func draw(_ dirtyRect: NSRect) {
        guard let shot, let p = current, let ctx = NSGraphicsContext.current?.cgContext else { return }
        let card = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: 6, yRadius: 6)
        RGBA(0x20, 0x20, 0x20, a: 0xF2).nsColor.setFill(); card.fill()
        NSColor.white.withAlphaComponent(0.4).setStroke(); card.stroke()
        let zr = CGRect(x: 4, y: 4, width: Self.zoom, height: Self.zoom)
        let half = Self.cells / 2
        let src = PixelRect(x: p.x - half, y: p.y - half, width: Self.cells, height: Self.cells)
        let inside = src.clamped(to: shot.bounds)
        ctx.saveGState()
        ctx.clip(to: zr)
        ctx.setFillColor(CGColor(gray: 0, alpha: 1)); ctx.fill(zr)
        if !inside.isEmpty, let crop = shot.cgImage.cropping(to: inside.cgRect) {
            let cell = Self.zoom / CGFloat(Self.cells)
            let r = CGRect(x: zr.minX + CGFloat(inside.x - src.x) * cell, y: zr.minY + CGFloat(inside.y - src.y) * cell,
                           width: CGFloat(inside.width) * cell, height: CGFloat(inside.height) * cell)
            ctx.interpolationQuality = .none
            ctx.saveGState()
            ctx.translateBy(x: r.minX, y: r.maxY); ctx.scaleBy(x: 1, y: -1)
            ctx.draw(crop, in: CGRect(origin: .zero, size: r.size))
            ctx.restoreGState()
        }
        ctx.setFillColor(RGBA(0x30, 0xA0, 0xFF, a: 0x55).cgColor)
        ctx.fill(CGRect(x: zr.minX, y: zr.midY - 0.5, width: zr.width, height: 1))
        ctx.fill(CGRect(x: zr.midX - 0.5, y: zr.minY, width: 1, height: zr.height))
        let cell = Self.zoom / CGFloat(Self.cells) + 1
        ctx.setStrokeColor(CGColor(gray: 1, alpha: 1)); ctx.setLineWidth(1)
        ctx.stroke(CGRect(x: zr.midX - cell / 2, y: zr.midY - cell / 2, width: cell, height: cell))
        ctx.restoreGState()
        let mono = NSFont.monospacedSystemFont(ofSize: 11, weight: .regular)
        NSAttributedString(string: "\(p.x), \(p.y)", attributes: [.font: mono, .foregroundColor: NSColor.white]).draw(at: CGPoint(x: 6, y: zr.maxY + 3))
        if showColor {
            let c = shot.color(at: p).opaque
            let sw = CGRect(x: 6, y: zr.maxY + 20, width: 11, height: 11)
            c.nsColor.setFill(); sw.fill()
            NSColor.white.withAlphaComponent(0.53).setStroke(); NSBezierPath(rect: sw.insetBy(dx: 0.5, dy: 0.5)).stroke()
            NSAttributedString(string: c.rgbHex, attributes: [.font: mono, .foregroundColor: NSColor.white.withAlphaComponent(0.8)])
                .draw(at: CGPoint(x: 22, y: zr.maxY + 18))
        }
    }
}

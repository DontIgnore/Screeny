import AppKit

/// Everything in the overlay HUD is dark and flipped (y down), like the Windows overlay panel.
class FlippedView: NSView {
    override var isFlipped: Bool { true }
}

enum Icon {
    case symbol(String, CGFloat)
    case text(String, NSFont)
    case sparkle
    case custom((CGRect, NSColor) -> Void)

    func draw(in r: CGRect, color: NSColor) {
        switch self {
        case .symbol(let name, let size):
            let cfg = NSImage.SymbolConfiguration(pointSize: size, weight: .regular)
            guard let img = NSImage(systemSymbolName: name, accessibilityDescription: nil)?.withSymbolConfiguration(cfg) else { return }
            let tinted = NSImage(size: img.size, flipped: false) { rect in
                img.draw(in: rect)
                color.set()
                rect.fill(using: .sourceAtop)
                return true
            }
            let s = img.size
            tinted.draw(in: CGRect(x: r.midX - s.width / 2, y: r.midY - s.height / 2, width: s.width, height: s.height))
        case .text(let t, let font):
            let s = NSAttributedString(string: t, attributes: [.font: font, .foregroundColor: color])
            let sz = s.size()
            s.draw(at: CGPoint(x: r.midX - sz.width / 2, y: r.midY - sz.height / 2))
        case .sparkle:
            // The ✨ path from ToolbarControl.xaml (16×16 box, y down).
            let p = NSBezierPath()
            let k = min(1, min(r.width, r.height) / 16)
            let ox = r.midX - 8.25 * k, oy = r.midY - 7.25 * k
            func pt(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: ox + x * k, y: oy + y * k) }
            p.move(to: pt(7, 2))
            p.curve(to: pt(13, 8), controlPoint1: pt(7.5, 6), controlPoint2: pt(9, 7.5))
            p.curve(to: pt(7, 14), controlPoint1: pt(9, 8.5), controlPoint2: pt(7.5, 10))
            p.curve(to: pt(1, 8), controlPoint1: pt(6.5, 10), controlPoint2: pt(5, 8.5))
            p.curve(to: pt(7, 2), controlPoint1: pt(5, 7.5), controlPoint2: pt(6.5, 6))
            p.close()
            p.move(to: pt(13, 0.5))
            p.curve(to: pt(15.5, 2.8), controlPoint1: pt(13.2, 2), controlPoint2: pt(13.8, 2.6))
            p.curve(to: pt(13, 5.1), controlPoint1: pt(13.8, 3), controlPoint2: pt(13.2, 3.6))
            p.curve(to: pt(10.5, 2.8), controlPoint1: pt(12.8, 3.6), controlPoint2: pt(12.2, 3))
            p.curve(to: pt(13, 0.5), controlPoint1: pt(12.2, 2.6), controlPoint2: pt(12.8, 2))
            p.close()
            color.setFill()
            p.fill()
        case .custom(let f): f(r, color)
        }
    }
}

/// Dark HUD button: transparent, hover/pressed fill, accent when checked (ToolbarControl.xaml BarButton/StripButton).
class HudButton: FlippedView {
    var icon: Icon? { didSet { needsDisplay = true } }
    var title: String? { didSet { invalidateIntrinsicContentSize(); needsDisplay = true } }
    var titleFont = NSFont.systemFont(ofSize: 12)
    var isChecked = false { didSet { if isChecked != oldValue { needsDisplay = true } } }
    var isEnabled = true { didSet { if isEnabled != oldValue { needsDisplay = true; alphaValue = isEnabled ? 1 : 0.35 } } }
    var accent = false
    var cornerRadius: CGFloat = 7
    var tip: String?
    var action: (() -> Void)?
    var onHover: ((Bool) -> Void)?
    /// Small dot in the bottom-right corner: the tool has options.
    var showsDot = false
    var leadingIcon: Icon?
    var size: CGSize

    private(set) var hovered = false
    private var pressed = false
    private var tracking: NSTrackingArea?

    init(size: CGSize, icon: Icon? = nil, title: String? = nil) {
        self.size = size
        self.icon = icon
        self.title = title
        super.init(frame: CGRect(origin: .zero, size: size))
    }
    required init?(coder: NSCoder) { fatalError() }

    override var intrinsicContentSize: NSSize {
        guard let title else { return size }
        let w = NSAttributedString(string: title, attributes: [.font: titleFont]).size().width
        return CGSize(width: max(size.width, ceil(w) + 14 + (leadingIcon == nil ? 0 : 20)), height: size.height)
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        if let t = tracking { removeTrackingArea(t) }
        tracking = NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self)
        addTrackingArea(tracking!)
    }

    override func mouseEntered(with event: NSEvent) {
        hovered = true; needsDisplay = true
        if let tip { HudTooltip.show(tip, for: self) }
        onHover?(true)
    }
    override func mouseExited(with event: NSEvent) {
        hovered = false; pressed = false; needsDisplay = true
        HudTooltip.hide(for: self)
        onHover?(false)
    }
    override func mouseDown(with event: NSEvent) { guard isEnabled else { return }; pressed = true; needsDisplay = true; HudTooltip.hide(for: self) }
    override func mouseUp(with event: NSEvent) {
        guard isEnabled, pressed else { return }
        pressed = false; needsDisplay = true
        if bounds.contains(convert(event.locationInWindow, from: nil)) { action?() }
    }
    override func rightMouseDown(with event: NSEvent) {}
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func resetCursorRects() { addCursorRect(bounds, cursor: .arrow) }

    var fillColor: NSColor? {
        if accent { return (pressed ? Palette.accentPressed : hovered ? Palette.accentHover : Palette.accent).nsColor }
        if isChecked { return Palette.accent.nsColor }
        if pressed { return Palette.overlayPressed.nsColor }
        if hovered && isEnabled { return Palette.overlayHover.nsColor }
        return nil
    }

    override func draw(_ dirtyRect: NSRect) {
        if let f = fillColor {
            f.setFill()
            NSBezierPath(roundedRect: bounds, xRadius: cornerRadius, yRadius: cornerRadius).fill()
        }
        let ink = (accent ? NSColor.white : Palette.overlayInk.nsColor)
        if let title {
            var textRect = bounds
            if let leadingIcon {
                let s = NSAttributedString(string: title, attributes: [.font: titleFont]).size()
                let total = 20 + s.width
                let x0 = bounds.midX - total / 2
                leadingIcon.draw(in: CGRect(x: x0, y: 0, width: 14, height: bounds.height), color: ink)
                textRect = CGRect(x: x0 + 20, y: 0, width: s.width, height: bounds.height)
            }
            Icon.text(title, titleFont).draw(in: textRect, color: ink)
        } else {
            icon?.draw(in: bounds, color: ink)
        }
        if showsDot {
            Palette.overlayInk70.nsColor.setFill()
            NSBezierPath(ovalIn: CGRect(x: bounds.maxX - 8, y: bounds.maxY - 8, width: 4, height: 4)).fill()
        }
    }
}

/// Dark tooltip drawn inside the overlay (system tooltips sit below a screen-saver-level window).
final class HudTooltip: FlippedView {
    private static var current: HudTooltip?
    private static weak var owner: NSView?
    private static var timer: Timer?
    private let text: NSAttributedString

    private init(_ s: String) {
        text = NSAttributedString(string: s, attributes: [.font: NSFont.systemFont(ofSize: 12), .foregroundColor: Palette.overlayInk.nsColor])
        let sz = text.boundingRect(with: CGSize(width: 360, height: 400), options: [.usesLineFragmentOrigin]).size
        super.init(frame: CGRect(x: 0, y: 0, width: ceil(sz.width) + 16, height: ceil(sz.height) + 10))
        wantsLayer = true
    }
    required init?(coder: NSCoder) { fatalError() }

    override func draw(_ dirtyRect: NSRect) {
        let p = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: 6, yRadius: 6)
        Palette.overlayTooltip.nsColor.setFill(); p.fill()
        Palette.overlayShotRing.nsColor.setStroke(); p.stroke()
        text.draw(with: bounds.insetBy(dx: 8, dy: 5), options: [.usesLineFragmentOrigin])
    }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    static func show(_ s: String, for view: NSView) {
        hideNow()
        owner = view
        timer = Timer.scheduledTimer(withTimeInterval: 0.35, repeats: false) { _ in
            guard let root = view.window?.contentView, owner === view else { return }
            let tip = HudTooltip(s)
            let r = view.convert(view.bounds, to: root)
            let rootFlipped = root.isFlipped
            var x = r.midX - tip.frame.width / 2
            x = clampInto(x, 4, root.bounds.width - tip.frame.width - 4)
            // Prefer above the control; flip below near the top edge.
            let aboveY = rootFlipped ? r.minY - tip.frame.height - 6 : r.maxY + 6
            let belowY = rootFlipped ? r.maxY + 6 : r.minY - tip.frame.height - 6
            var y = aboveY
            if rootFlipped ? aboveY < 4 : aboveY + tip.frame.height > root.bounds.height - 4 { y = belowY }
            tip.frame.origin = CGPoint(x: x, y: y)
            root.addSubview(tip)
            current = tip
        }
    }

    static func hide(for view: NSView) { if owner === view { hideNow() } }
    static func hideNow() { timer?.invalidate(); timer = nil; current?.removeFromSuperview(); current = nil; owner = nil }
}

/// Rounded dark card with a soft shadow: toolbar and flyouts.
class HudCard: FlippedView {
    var radius: CGFloat = 12
    override init(frame: NSRect) {
        super.init(frame: frame)
        wantsLayer = true
        shadow = {
            let s = NSShadow()
            s.shadowBlurRadius = 12; s.shadowOffset = CGSize(width: 0, height: -6)
            s.shadowColor = NSColor.black.withAlphaComponent(0.55)
            return s
        }()
    }
    required init?(coder: NSCoder) { fatalError() }

    override func draw(_ dirtyRect: NSRect) {
        let p = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: radius, yRadius: radius)
        Palette.overlayPanel.nsColor.setFill(); p.fill()
        Palette.overlayShotRing.nsColor.setStroke(); p.lineWidth = 1; p.stroke()
    }
    override func resetCursorRects() { addCursorRect(bounds, cursor: .arrow) }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseDown(with event: NSEvent) {}
    override func rightMouseDown(with event: NSEvent) {}
    override func scrollWheel(with event: NSEvent) {}
}

final class HudSeparator: FlippedView {
    init(height: CGFloat = 20, margin: CGFloat = 5) { super.init(frame: CGRect(x: 0, y: 0, width: 1 + 2 * margin, height: height)) }
    required init?(coder: NSCoder) { fatalError() }
    override func draw(_ dirtyRect: NSRect) {
        Palette.overlayDivider.nsColor.setFill()
        CGRect(x: bounds.midX - 0.5, y: 0, width: 1, height: bounds.height).fill()
    }
}

/// A small dark rounded label (size label, hints, badges).
final class HudLabel: FlippedView {
    var text = "" { didSet { resize() } }
    var font = NSFont.systemFont(ofSize: 12)
    var background = RGBA(0x20, 0x20, 0x20, a: 0xCC)
    var padding = CGSize(width: 6, height: 2)
    var radius: CGFloat = 4
    var chip: RGBA?
    var trailingIcon: (Icon, NSColor)?
    var maxWidth: CGFloat = 900

    private var attributed: NSAttributedString { NSAttributedString(string: text, attributes: [.font: font, .foregroundColor: NSColor.white]) }

    func resize() {
        let sz = attributed.boundingRect(with: CGSize(width: maxWidth, height: 1000), options: [.usesLineFragmentOrigin]).size
        let extra = (chip == nil ? 0 : 17) + (trailingIcon == nil ? 0 : 16)
        setFrameSize(CGSize(width: ceil(sz.width) + 2 * padding.width + CGFloat(extra), height: ceil(sz.height) + 2 * padding.height))
        needsDisplay = true
    }

    override func hitTest(_ point: NSPoint) -> NSView? { nil }

    override func draw(_ dirtyRect: NSRect) {
        background.nsColor.setFill()
        NSBezierPath(roundedRect: bounds, xRadius: radius, yRadius: radius).fill()
        var x = padding.width
        if let c = chip {
            let r = CGRect(x: x, y: bounds.midY - 5.5, width: 11, height: 11)
            c.nsColor.setFill(); NSBezierPath(roundedRect: r, xRadius: 2, yRadius: 2).fill()
            NSColor.white.withAlphaComponent(0.53).setStroke(); NSBezierPath(roundedRect: r.insetBy(dx: 0.5, dy: 0.5), xRadius: 2, yRadius: 2).stroke()
            x += 17
        }
        attributed.draw(with: CGRect(x: x, y: padding.height, width: bounds.width - x - padding.width, height: bounds.height), options: [.usesLineFragmentOrigin])
        if let (icon, color) = trailingIcon {
            icon.draw(in: CGRect(x: bounds.maxX - padding.width - 14, y: 0, width: 14, height: bounds.height), color: color)
        }
    }
}

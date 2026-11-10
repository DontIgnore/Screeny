import AppKit

/// Window palette (Themes/Dark.xaml, Light.xaml): warm ink on warm paper, follows the system appearance.
/// Use these dynamic colors in every regular window so they switch with light/dark mode.
enum Theme {
    private static func dyn(_ light: RGBA, _ dark: RGBA) -> NSColor {
        NSColor(name: nil) { a in
            (a.bestMatch(from: [.darkAqua, .aqua]) == .darkAqua ? dark : light).nsColor
        }
    }
    private static func ink(_ alpha: UInt8) -> NSColor { dyn(RGBA(0x29, 0x25, 0x24, a: alpha), RGBA(0xFF, 0xF7, 0xED, a: alpha)) }

    static let bg = dyn(RGBA(hex: 0xFFF7ED), RGBA(hex: 0x292524))
    static let ink = Theme.ink(0xFF)
    static let ink70 = Theme.ink(0xB3)
    static let ink50 = Theme.ink(0x80)
    static let ink20 = Theme.ink(0x33)
    static let ink10 = Theme.ink(0x1A)
    static let ink05 = Theme.ink(0x0D)
    static let card = Theme.ink(0x0D)
    static let cardHover = Theme.ink(0x1A)
    static let cardPressed = Theme.ink(0x24)
    static let stroke = Theme.ink(0x1A)
    static let strokeStrong = Theme.ink(0x33)
    static let popup = dyn(RGBA(hex: 0xFFFCF8), RGBA(hex: 0x332F2D))
    static let thumb = dyn(RGBA(hex: 0xF3EBE1), RGBA(hex: 0x211E1D))
    static let dangerSubtle = dyn(RGBA(0xFF, 0x3B, 0x30, a: 0x1F), RGBA(0xFF, 0x3B, 0x30, a: 0x26))
    static let dangerText = dyn(RGBA(hex: 0xD92D20), RGBA(hex: 0xFF6B61))
    static let accent = Palette.accent.nsColor
    static let accentHover = Palette.accentHover.nsColor
    static let accentPressed = Palette.accentPressed.nsColor
    static let danger = Palette.danger.nsColor

    static let h1 = NSFont.systemFont(ofSize: 16, weight: .semibold)
    static let body = NSFont.systemFont(ofSize: 13)
    static let caption = NSFont.systemFont(ofSize: 12)
    static let sectionLabel = NSFont.systemFont(ofSize: 11, weight: .semibold)
    static let mono = NSFont.monospacedSystemFont(ofSize: 12, weight: .regular)

    /// Standard window look: our background color, title bar blended into it.
    static func style(_ w: NSWindow) {
        w.backgroundColor = bg
        w.titlebarAppearsTransparent = true
        w.isMovableByWindowBackground = false
    }
}

/// Rounded button in the window style (Theme.xaml Button / AccentButton / danger variant).
final class ThemeButton: NSButton {
    enum Kind { case normal, accent, danger, subtle }
    var kind: Kind = .normal { didSet { needsDisplay = true } }
    private var hovered = false, pressed = false
    var handler: (() -> Void)?

    convenience init(_ title: String, kind: Kind = .normal, handler: (() -> Void)? = nil) {
        self.init(frame: .zero)
        self.title = title
        self.kind = kind
        self.handler = handler
        isBordered = false
        bezelStyle = .regularSquare
        font = Theme.body
        target = self
        action = #selector(fire)
        setButtonType(.momentaryChange)
    }

    @objc private func fire() { handler?() }

    override var intrinsicContentSize: NSSize {
        let w = NSAttributedString(string: title, attributes: [.font: font ?? Theme.body]).size().width
        return CGSize(width: ceil(w) + 28 + (image == nil ? 0 : 20), height: 32)
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect], owner: self))
    }
    override func mouseEntered(with event: NSEvent) { hovered = true; needsDisplay = true }
    override func mouseExited(with event: NSEvent) { hovered = false; needsDisplay = true }
    override func mouseDown(with event: NSEvent) { pressed = true; needsDisplay = true; super.mouseDown(with: event); pressed = false; needsDisplay = true }

    override func draw(_ dirtyRect: NSRect) {
        let fill: NSColor, text: NSColor
        switch kind {
        case .accent: fill = pressed ? Theme.accentPressed : hovered ? Theme.accentHover : Theme.accent; text = .white
        case .danger: fill = pressed ? Palette.danger.nsColor.blended(withFraction: 0.15, of: .black)! : hovered ? RGBA(hex: 0xFF5A50).nsColor : Theme.danger; text = .white
        case .subtle: fill = pressed ? Theme.cardPressed : hovered ? Theme.cardHover : .clear; text = Theme.ink
        case .normal: fill = pressed ? Theme.cardPressed : hovered ? Theme.cardHover : Theme.card; text = Theme.ink
        }
        let path = NSBezierPath(roundedRect: bounds.insetBy(dx: 0.5, dy: 0.5), xRadius: 8, yRadius: 8)
        (isEnabled ? fill : fill.withAlphaComponent(0.4)).setFill()
        path.fill()
        if kind == .normal { Theme.stroke.setStroke(); path.stroke() }
        let attrs: [NSAttributedString.Key: Any] = [.font: font ?? Theme.body, .foregroundColor: isEnabled ? text : text.withAlphaComponent(0.4)]
        let s = NSAttributedString(string: title, attributes: attrs)
        let sz = s.size()
        var x = bounds.midX - sz.width / 2
        if let img = image {
            x += 10
            let r = CGRect(x: x - 20, y: bounds.midY - 7, width: 14, height: 14)
            img.isTemplate = true
            let tinted = NSImage(size: r.size, flipped: false) { rr in img.draw(in: rr); text.set(); rr.fill(using: .sourceAtop); return true }
            tinted.draw(in: r)
        }
        s.draw(at: CGPoint(x: x, y: bounds.midY - sz.height / 2))
    }
}

/// Label helper.
func themeLabel(_ text: String, font: NSFont = Theme.body, color: NSColor = Theme.ink, wraps: Bool = false) -> NSTextField {
    let l = wraps ? NSTextField(wrappingLabelWithString: text) : NSTextField(labelWithString: text)
    l.font = font
    l.textColor = color
    l.isSelectable = false
    return l
}

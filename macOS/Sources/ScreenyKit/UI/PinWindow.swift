import AppKit
import UniformTypeIdentifiers

/// Size and zoom math for pins (pure, tested).
enum PinGeometry {
    static let minZoom: CGFloat = 0.2, maxZoom: CGFloat = 5, minOpacity: CGFloat = 0.2

    /// Zoom (1 = logical size) that fits `size` into `fraction` of `screen`, never above 1.
    static func fitZoom(_ size: CGSize, in screen: CGSize, fraction: CGFloat = 0.8) -> CGFloat {
        guard size.width > 0, size.height > 0 else { return 1 }
        return min(1, screen.width * fraction / size.width, screen.height * fraction / size.height)
    }

    /// 20–500 %, and never larger than the whole desktop (Windows MaxScale).
    static func clampZoom(_ zoom: CGFloat, size: CGSize, desktop: CGSize) -> CGFloat {
        var hi = maxZoom
        if size.width > 0, size.height > 0, desktop.width > 0, desktop.height > 0 {
            hi = max(minZoom, min(maxZoom, desktop.width / size.width, desktop.height / size.height))
        }
        return max(minZoom, min(hi, zoom))
    }

    /// New frame of `newSize` keeping `anchor` at the same relative spot (center when nil).
    static func zoomedFrame(_ old: CGRect, to newSize: CGSize, anchor: CGPoint?) -> CGRect {
        let a = anchor ?? CGPoint(x: old.midX, y: old.midY)
        let fx = old.width > 0 ? (a.x - old.minX) / old.width : 0.5
        let fy = old.height > 0 ? (a.y - old.minY) / old.height : 0.5
        return CGRect(x: (a.x - fx * newSize.width).rounded(), y: (a.y - fy * newSize.height).rounded(),
                      width: newSize.width.rounded(), height: newSize.height.rounded())
    }

    static func clampOpacity(_ v: CGFloat) -> CGFloat { max(minOpacity, min(1, v)) }
}

/// A picture pinned on top of all windows (Pin/PinWindow.xaml.cs): drag to move, wheel zooms, ⌘+wheel opacity.
final class PinWindow: NSPanel, NSWindowDelegate {
    /// Open pins; captures exclude `windowNumbers`.
    private(set) static var all: [PinWindow] = []
    static var windowNumbers: [Int] { all.map(\.windowNumber) }

    let image: CGImage
    private let logicalSize: CGSize
    private weak var services: AppServices?
    private(set) var zoom: CGFloat = 1
    private let pinView = PinView()
    private let badge = HudLabel()
    private var badgeTimer: Timer?

    @discardableResult
    static func open(image: CGImage, scale: CGFloat, services: AppServices, near: CGPoint? = nil) -> PinWindow {
        let pin = PinWindow(image: image, scale: scale, services: services, near: near)
        all.append(pin)
        NSApp.activate()
        pin.makeKeyAndOrderFront(nil)
        return pin
    }

    private init(image: CGImage, scale: CGFloat, services: AppServices, near: CGPoint?) {
        self.image = image
        let s = max(scale, 0.01)
        logicalSize = CGSize(width: CGFloat(image.width) / s, height: CGFloat(image.height) / s)
        self.services = services
        let screen = near.flatMap { p in NSScreen.screens.first { $0.frame.contains(CGPoint(x: p.x, y: p.y - 1)) } } ?? NSScreen.main ?? NSScreen.screens[0]
        let vis = screen.visibleFrame
        let z = PinGeometry.fitZoom(logicalSize, in: vis.size)
        let size = CGSize(width: (logicalSize.width * z).rounded(), height: (logicalSize.height * z).rounded())
        // `near` is the top-left corner in screen coordinates (y up).
        let origin = near.map { CGPoint(x: $0.x, y: $0.y - size.height) }
            ?? CGPoint(x: (vis.midX - size.width / 2).rounded(), y: (vis.midY - size.height / 2).rounded())
        super.init(contentRect: CGRect(origin: origin, size: size), styleMask: [.borderless, .nonactivatingPanel],
                   backing: .buffered, defer: false)
        zoom = z
        level = .floating
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        isOpaque = false
        backgroundColor = .clear
        hasShadow = true
        hidesOnDeactivate = false
        isReleasedWhenClosed = false
        delegate = self
        title = "Screeny Pin"

        pinView.pin = self
        pinView.autoresizingMask = [.width, .height]
        contentView = pinView
        badge.font = .monospacedSystemFont(ofSize: 11, weight: .regular)
        badge.background = Palette.overlayPanel
        badge.padding = CGSize(width: 7, height: 3)
        badge.radius = 6
        badge.isHidden = true
        pinView.addSubview(badge)
        updateInterpolation()
    }

    override var canBecomeKey: Bool { true }

    func windowWillClose(_ notification: Notification) {
        badgeTimer?.invalidate()
        Self.all.removeAll { $0 === self }
    }

    // MARK: Zoom & opacity

    private var desktop: CGSize { NSScreen.screens.reduce(CGRect.null) { $0.union($1.frame) }.size }

    func zoom(to value: CGFloat, anchor: CGPoint? = nil) {
        zoom = PinGeometry.clampZoom(value, size: logicalSize, desktop: desktop)
        let size = CGSize(width: logicalSize.width * zoom, height: logicalSize.height * zoom)
        setFrame(PinGeometry.zoomedFrame(frame, to: size, anchor: anchor), display: true)
        updateInterpolation()
        showBadge("\(Int((zoom * 100).rounded())) %")
    }

    func setOpacity(_ v: CGFloat) {
        alphaValue = PinGeometry.clampOpacity(v)
        showBadge(Loc.f("Pin.OpacityBadge", Int((alphaValue * 100).rounded())))
    }

    private func updateInterpolation() { pinView.sharp = abs(zoom - 1) < 0.001 }

    private func showBadge(_ text: String) {
        badge.text = text
        badge.frame.origin = CGPoint(x: 8, y: 8)
        badge.alphaValue = 1
        badge.isHidden = false
        badgeTimer?.invalidate()
        badgeTimer = Timer.scheduledTimer(withTimeInterval: 1.2, repeats: false) { [weak self] _ in
            NSAnimationContext.runAnimationGroup({ $0.duration = 0.25; self?.badge.animator().alphaValue = 0 })
        }
    }

    // MARK: Commands

    @objc func copy(_ sender: Any?) {
        if services?.copyImage(image, scale: 1) == false { showBadge(Loc.t("Common.ClipboardBusy")) }
    }

    @objc func save(_ sender: Any?) {
        let panel = NSSavePanel()
        panel.title = Loc.t("Common.SaveScreenshotTitle")
        panel.allowedContentTypes = [.png, .jpeg]
        panel.isExtensionHidden = false
        let stamp = DateFormatter()
        stamp.dateFormat = "yyyyMMdd_HHmmss"
        panel.nameFieldStringValue = "Screeny_\(stamp.string(from: Date())).png"
        level = .normal // the save panel must not sit under the pin
        defer { level = .floating }
        guard panel.runModal() == .OK, let url = panel.url else { return }
        let ext = url.pathExtension.lowercased()
        let type: UTType = ext == "jpg" || ext == "jpeg" ? .jpeg : .png
        do {
            guard let data = PixelImage.encode(image, type: type) else { throw CocoaError(.fileWriteUnknown) }
            try data.write(to: url, options: .atomic)
        } catch {
            Log.error("Pin save failed", error)
            services?.toast(Loc.f("App.SaveShotFailed", error.localizedDescription))
        }
    }

    @objc fileprivate func resetZoom(_ sender: Any?) { zoom(to: 1) }
    @objc fileprivate func pickOpacity(_ sender: NSMenuItem) { setOpacity(CGFloat(sender.tag) / 100) }
    @objc fileprivate func closePin(_ sender: Any?) { close() }

    fileprivate func contextMenu() -> NSMenu {
        let m = NSMenu()
        func item(_ title: String, _ action: Selector, _ key: String = "", _ mods: NSEvent.ModifierFlags = .command) -> NSMenuItem {
            let i = NSMenuItem(title: title, action: action, keyEquivalent: key)
            i.keyEquivalentModifierMask = mods
            i.target = self
            return i
        }
        m.addItem(item(Loc.t("Common.Copy"), #selector(copy(_:)), "c"))
        m.addItem(item(Loc.t("Pin.Save"), #selector(save(_:)), "s"))
        m.addItem(.separator())
        m.addItem(item(Loc.t("Pin.ResetZoom"), #selector(resetZoom(_:))))
        let op = NSMenuItem(title: Loc.t("Pin.Opacity"), action: nil, keyEquivalent: "")
        let sub = NSMenu()
        for p in [100, 75, 50, 25] {
            let i = item("\(p) %", #selector(pickOpacity(_:)))
            i.tag = p
            i.state = Int((alphaValue * 100).rounded()) == p ? .on : .off
            sub.addItem(i)
        }
        op.submenu = sub
        m.addItem(op)
        m.addItem(.separator())
        m.addItem(item(Loc.t("Common.Close"), #selector(closePin(_:)), "\u{1b}", []))
        return m
    }

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        let mods = event.modifierFlags.intersection(.deviceIndependentFlagsMask)
        if mods == .command {
            switch event.charactersIgnoringModifiers?.lowercased() {
            case "c": copy(nil); return true
            case "s": save(nil); return true
            default: break
            }
        }
        return super.performKeyEquivalent(with: event)
    }

    override func cancelOperation(_ sender: Any?) { close() }
}

/// Picture + 1 px frame (white 50 %, accent while hovered).
private final class PinView: FlippedView {
    weak var pin: PinWindow?
    var sharp = true { didSet { needsDisplay = true } }
    private var hovered = false

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self))
    }
    override func mouseEntered(with event: NSEvent) { hovered = true; needsDisplay = true }
    override func mouseExited(with event: NSEvent) { hovered = false; needsDisplay = true }
    override func resetCursorRects() { addCursorRect(bounds, cursor: .openHand) }

    override func draw(_ dirtyRect: NSRect) {
        guard let pin, let ctx = NSGraphicsContext.current?.cgContext else { return }
        ctx.saveGState()
        ctx.interpolationQuality = sharp ? .none : .high
        // Flipped view: flip back so CGImage draws upright.
        ctx.translateBy(x: 0, y: bounds.height)
        ctx.scaleBy(x: 1, y: -1)
        ctx.draw(pin.image, in: bounds)
        ctx.restoreGState()
        (hovered ? Palette.accent.nsColor : NSColor.white.withAlphaComponent(0.5)).setStroke()
        let ring = NSBezierPath(rect: bounds.insetBy(dx: 0.5, dy: 0.5))
        ring.lineWidth = 1
        ring.stroke()
    }

    override func mouseDown(with event: NSEvent) {
        if event.clickCount == 2 { pin?.close(); return }
        window?.performDrag(with: event)
    }

    override func scrollWheel(with event: NSEvent) {
        guard let pin else { return }
        let dy = event.scrollingDeltaY
        guard dy != 0 else { return }
        // Mouse wheel: one notch = one Windows step; trackpad: ~10 points per step.
        let steps = event.hasPreciseScrollingDeltas ? dy / 10 : (dy > 0 ? 1 : -1)
        if event.modifierFlags.contains(.command) {
            pin.setOpacity(pin.alphaValue + 0.1 * steps)
        } else {
            pin.zoom(to: pin.zoom * pow(1.1, steps), anchor: NSEvent.mouseLocation)
        }
    }

    override func keyDown(with event: NSEvent) {
        if event.keyCode == 53 { pin?.close() } else { super.keyDown(with: event) }
    }

    override func menu(for event: NSEvent) -> NSMenu? { pin?.contextMenu() }
}

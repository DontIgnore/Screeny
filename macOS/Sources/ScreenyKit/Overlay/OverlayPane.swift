import AppKit

/// Another display's part of the virtual screen: shows the frozen picture and the markup of the overlay view that lives
/// on the active display, and sends its mouse and keys there. The session moves that view to whichever display holds
/// the selection, so the toolbar and options are always on the right screen.
final class OverlayPane: FlippedView {
    unowned let master: OverlayView
    let canvas = CanvasView()
    private let shotView: ShotView

    /// `area` is this display's frame in AppKit global points.
    init(master: OverlayView, area: CGRect, virtualFrame u: CGRect, image: CGImage) {
        self.master = master
        shotView = ShotView(image: image, crop: CGRect(x: (area.minX - u.minX) / u.width, y: (area.minY - u.minY) / u.height,
                                                       width: area.width / u.width, height: area.height / u.height))
        super.init(frame: CGRect(origin: .zero, size: area.size))
        autoresizingMask = [.width, .height]
        shotView.frame = bounds
        canvas.frame = bounds
        canvas.model = master
        canvas.offset = CGPoint(x: area.minX - u.minX, y: u.maxY - area.maxY)
        addSubview(shotView)
        addSubview(canvas)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseMoved, .activeAlways, .inVisibleRect, .cursorUpdate], owner: self))
    }
    required init?(coder: NSCoder) { fatalError() }

    func invalidateCanvas() { canvas.needsDisplay = true }

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseDown(with e: NSEvent) { master.mouseDown(with: e) }
    override func mouseDragged(with e: NSEvent) { master.mouseDragged(with: e) }
    override func mouseUp(with e: NSEvent) { master.mouseUp(with: e) }
    override func mouseMoved(with e: NSEvent) { master.mouseMoved(with: e) }
    override func rightMouseDown(with e: NSEvent) { master.rightMouseDown(with: e) }
    override func scrollWheel(with e: NSEvent) { master.scrollWheel(with: e) }
    override func cursorUpdate(with e: NSEvent) { master.cursorUpdate(with: e) }
    override func keyDown(with e: NSEvent) { master.keyDown(with: e) }
    override func flagsChanged(with e: NSEvent) { master.flagsChanged(with: e) }
    override func performKeyEquivalent(with e: NSEvent) -> Bool {
        guard window?.firstResponder === self, e.modifierFlags.contains(.command) else { return super.performKeyEquivalent(with: e) }
        master.keyDown(with: e)
        return true
    }
}

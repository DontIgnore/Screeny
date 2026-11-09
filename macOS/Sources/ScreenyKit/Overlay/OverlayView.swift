import AppKit
import UniformTypeIdentifiers

struct CaptureResult {
    var image: CGImage
    var action: CaptureAction
    var region: PixelRect
    /// The region on screen in AppKit global coordinates (points): pins open right where the selection was.
    var screenRect: CGRect
    var scale: CGFloat
    var saveURL: URL?
    var text: String?
    var annotationsJSON: String?
    var original: CGImage?
}

/// Borderless full-screen window above everything, including the menu bar and the Dock.
final class OverlayWindow: NSPanel {
    init(screen: NSScreen) {
        super.init(contentRect: screen.frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        level = .screenSaver
        isOpaque = true
        backgroundColor = .black
        hasShadow = false
        hidesOnDeactivate = false
        isReleasedWhenClosed = false
        acceptsMouseMovedEvents = true
        isMovable = false
        collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary, .stationary, .ignoresCycle]
        setFrame(screen.frame, display: false)
    }
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

enum OverlayMode { case selecting, editing }

/// One display's frozen screenshot with selection, drawing and text mode (Overlay/OverlayWindow.xaml.cs).
/// Coordinates: the view is flipped, in points; screenshot pixels = points × scale.
final class OverlayView: FlippedView, CanvasModel, AnnotationEditorHost, FlyoutHost {
    static let dragThreshold = 3

    let shotInfo: DisplayShot
    let settings: AppSettings
    var shot: PixelImage { shotInfo.image }
    var pixelScale: CGFloat { shotInfo.scale }
    private let selection: SelectionModel
    private(set) var editor: AnnotationEditor!
    private let shotView: ShotView
    private let canvas = CanvasView()
    let toolbar = ToolbarView()
    private let sizeLabel = HudLabel()
    private let textHint = HudLabel()
    private let magnifier = MagnifierView()
    private let pipetteBadge = HudLabel()
    private let codePopup = CodePopupView()
    private var mode = OverlayMode.selecting
    private var dragAnchor: PixelPoint?
    private var thresholdPassed = false
    private var draggingHandle = false
    private(set) var closing = false
    var dialogOpen = false
    private var toolbarBelow = true
    private var edgeSnapper: EdgeSnapper?
    private var frameGuides: [SnapGuide] = []
    private var hintTimer: Timer?, badgeTimer: Timer?

    // Text mode
    private var textMode = false
    private var ocrGeneration = 0
    private var ocr: OcrLayout?
    private var textSelection = Set<Int>()
    private var textAnchor: Int?
    private var textPress: CGPoint?
    private var textDragFromWord = false
    private var textDragging = false
    private var textBase = Set<Int>()
    private var copyWhenReady = false
    private var codes: [DetectedCode] = []
    private var openCode: Int?

    /// Called once: a result, or nil when cancelled.
    var finished: ((CaptureResult?) -> Void)?
    /// Selection started on this display: others reset theirs.
    var becameActive: ((OverlayView) -> Void)?

    // CanvasModel
    var document: AnnotationDocument { editor.document }
    var annotationClip: CGRect? { selection.hasSelection ? selection.rect.cgRect : nil }
    var hole: PixelRect? { selection.hasSelection ? selection.rect : nil }
    var hint: PixelRect?
    var showFrame: Bool { selection.hasSelection }
    var showHandles: Bool { (mode == .editing || draggingHandle) && !textMode }
    var dimAlpha: UInt8 { 0x80 }
    var ruler: RulerMeasurement? { editor.ruler.measurement }
    var guides: [SnapGuide] { frameGuides.isEmpty ? editor.snapper.guides : frameGuides }
    var textLayer: TextLayerState?

    init(shot: DisplayShot, settings: AppSettings) {
        shotInfo = shot
        self.settings = settings
        selection = SelectionModel(bounds: shot.image.bounds)
        shotView = ShotView(image: shot.image.cgImage)
        super.init(frame: CGRect(origin: .zero, size: shot.screen.frame.size))
        wantsLayer = true
        editor = AnnotationEditor(host: self, toolbar: toolbar, source: shot.image, settings: settings,
                                  beautifyOn: settings.beautifyEnabled, beautify: settings.beautify, persistBeautify: true,
                                  unitScale: shot.scale)
        editor.guidesChanged = { [weak self] in self?.canvas.needsDisplay = true }
        shotView.frame = bounds
        canvas.frame = bounds
        canvas.model = self
        addSubview(shotView)
        addSubview(canvas)
        sizeLabel.isHidden = true
        textHint.isHidden = true
        textHint.background = RGBA(0x20, 0x20, 0x20, a: 0xE6)
        textHint.padding = CGSize(width: 10, height: 5)
        textHint.radius = 6
        textHint.maxWidth = 700
        pipetteBadge.isHidden = true
        pipetteBadge.background = RGBA(0x29, 0x25, 0x24, a: 0xF2)
        pipetteBadge.padding = CGSize(width: 8, height: 3)
        pipetteBadge.radius = 6
        magnifier.isHidden = true
        magnifier.showColor = settings.isOn(.pipette)
        codePopup.isHidden = true
        codePopup.copyAction = { [weak self] in self?.copyOpenCode() }
        codePopup.openAction = { [weak self] in self?.openCodeLink() }
        for v in [sizeLabel, textHint, magnifier, pipetteBadge, toolbar, codePopup] as [NSView] { addSubview(v) }
        toolbar.isHidden = true
        editor.flyouts.attach(self)
        toolbar.actionRequested = { [weak self] a in
            guard let self else { return }
            if a == .save { self.saveAs() } else if self.textMode && a == .copyExplicit { self.copyText() } else { self.complete(a) }
        }
        toolbar.textModeRequested = { [weak self] in self?.toggleTextMode() }
        toolbar.cancelRequested = { [weak self] in self?.cancel() }
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseMoved, .activeAlways, .inVisibleRect, .cursorUpdate], owner: self))
    }
    required init?(coder: NSCoder) { fatalError() }

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    // MARK: Coordinates

    private func pixel(_ p: CGPoint) -> PixelPoint {
        PixelPoint(x: clamp(Int(floor(p.x * pixelScale)), 0, shot.width - 1), y: clamp(Int(floor(p.y * pixelScale)), 0, shot.height - 1))
    }
    private func exact(_ p: CGPoint) -> CGPoint { CGPoint(x: p.x * pixelScale, y: p.y * pixelScale) }
    private func local(_ e: NSEvent) -> CGPoint { convert(e.locationInWindow, from: nil) }
    private var mouseLocal: CGPoint { window.map { convert($0.mouseLocationOutsideOfEventStream, from: nil) } ?? .zero }

    /// Tool point: fractional pixels clamped into the selection (drawing happens only inside).
    private func toolPoint(_ p: CGPoint) -> CGPoint {
        let e = exact(p), r = selection.rect
        return CGPoint(x: clamp(e.x, CGFloat(r.x), CGFloat(r.right)), y: clamp(e.y, CGFloat(r.y), CGFloat(r.bottom)))
    }

    func toPoints(_ r: CGRect) -> CGRect { CGRect(x: r.minX / pixelScale, y: r.minY / pixelScale, width: r.width / pixelScale, height: r.height / pixelScale) }

    /// Usable area of this display without the menu bar and Dock, in view points.
    private var workArea: CGRect {
        let s = shotInfo.screen
        let v = s.visibleFrame, f = s.frame
        return CGRect(x: v.minX - f.minX, y: f.maxY - v.maxY, width: v.width, height: v.height)
    }
    private var monitorPixels: PixelRect { shot.bounds }
    private var handleTolerance: Int { Int((6 * pixelScale).rounded()) }

    private func isFromHud(_ e: NSEvent) -> Bool {
        guard let hit = hitTest(convert(e.locationInWindow, from: nil).applying(.identity)) else { return false }
        return hit !== self
    }

    // MARK: Host protocols

    var optionsEnabled: Bool { selection.hasSelection && mode == .editing && !textMode }
    var canUsePipette: Bool { mode == .editing && !textMode && !closing }
    var isClosing: Bool { closing }
    var hostHasGesture: Bool { hasGesture }
    func pixelToHud(_ p: CGPoint) -> CGPoint { CGPoint(x: p.x / pixelScale, y: p.y / pixelScale) }
    func pixelsToHud(_ l: Double) -> Double { l / pixelScale }
    var hudView: NSView { self }
    var snapArea: CGRect { selection.hasSelection ? selection.rect.cgRect : .null }
    func focusSurface() { window?.makeFirstResponder(self) }
    func refreshHover() { updateHover(mouseLocal) }
    func pipetteChanged(_ on: Bool) {
        if !on { magnifier.isHidden = true } else { updateMagnifier(pixel(mouseLocal)) }
    }
    func beautifyChanged() { refreshSelectionVisuals() }
    func redraw() { canvas.needsDisplay = true }
    var flyoutLayer: NSView { self }
    var flyoutWorkArea: CGRect { workArea }
    var toolbarFlyoutsBelow: Bool { toolbarBelow }
    var toolbarFrame: CGRect { toolbar.frame }
    func shapeRect(_ a: Annotation) -> CGRect? { a.bounds.isNull ? nil : toPoints(a.bounds) }

    private var hasGesture: Bool { editor.hasGesture || draggingHandle || dragAnchor != nil || textPress != nil }

    // MARK: Showing

    func didShow() {
        window?.makeFirstResponder(self)
        updateHover(mouseLocal)
    }

    // MARK: Mouse

    override func mouseDown(with e: NSEvent) {
        let p = local(e)
        if textMode { textMouseDown(e, p); return }
        editor.commitTextEdit()
        let px = pixel(p)
        if editor.pipetteOn { editor.pickColor(px); return }
        becameActive?(self)
        let handle = selection.hitTest(px, tolerance: handleTolerance)
        if mode == .editing {
            let resize = handle != .none && handle != .move
            let moveSel = handle == .move && toolbar.currentTool == .select && document.hitTest(exact(p)) == nil
            if resize || moveSel { editor.deselect(); startHandleDrag(handle, px); return }
            if handle == .move { _ = editor.beginGesture(toolPoint(p)) } else { editor.deselect() }
            return
        }
        if selection.hasSelection && handle != .none { startHandleDrag(handle, px); return }
        dragAnchor = px
        thresholdPassed = false
        edgeSnapper?.resetCache()
    }

    private func startHandleDrag(_ h: Handle, _ p: PixelPoint) {
        editor.flyouts.closeAll()
        selection.beginDrag(h, at: p)
        draggingHandle = true
        edgeSnapper?.resetCache()
    }

    override func mouseDragged(with e: NSEvent) {
        let p = local(e)
        if textMode { textMouseMove(p, dragging: true); return }
        let px = pixel(p)
        if editor.hasGesture { editor.moveGesture(toolPoint(p)); return }
        if draggingHandle {
            frameGuides = []
            selection.drag(to: px, snap: snapActive ? snapFrameEdge : nil)
            refreshSelectionVisuals()
            return
        }
        if let a = dragAnchor {
            if thresholdPassed || abs(px.x - a.x) >= Self.dragThreshold || abs(px.y - a.y) >= Self.dragThreshold {
                thresholdPassed = true
                frameGuides = []
                selection.rect = SelectionModel.fromPoints(a, px, snap: snapActive ? snapFrameEdge : nil)
                hint = nil
                refreshSelectionVisuals()
            }
            updateMagnifier(px)
        }
    }

    override func mouseMoved(with e: NSEvent) {
        let p = local(e)
        if textMode { textMouseMove(p, dragging: false); return }
        if let hit = hitTest(p), hit !== self, hit !== canvas {
            NSCursor.arrow.set()
            if editor.pipetteOn { magnifier.isHidden = true }
            return
        }
        updateHover(p)
    }

    override func mouseUp(with e: NSEvent) {
        let p = local(e)
        if textMode { textMouseUp(e, p); return }
        if editor.endGesture(toolPoint(p)) { return }
        if draggingHandle {
            selection.endDrag()
            draggingHandle = false
            frameGuides = []
            refreshSelectionVisuals()
            if mode == .selecting { enterEditing() }
            return
        }
        if let a = dragAnchor {
            let click = !thresholdPassed
            dragAnchor = nil; thresholdPassed = false; frameGuides = []
            if click { selection.rect = WindowInfo.topmost(shotInfo.windows, at: a)?.rect ?? monitorPixels }
            if selection.hasSelection { enterEditing() } else { refreshSelectionVisuals() }
        }
    }

    override func rightMouseDown(with e: NSEvent) { stepBack() }

    private var wheelAccum: CGFloat = 0
    override func scrollWheel(with e: NSEvent) {
        guard mode == .editing, !textMode else { return }
        wheelAccum += e.hasPreciseScrollingDeltas ? e.scrollingDeltaY / 8 : e.scrollingDeltaY
        while abs(wheelAccum) >= 1 {
            let d = wheelAccum > 0 ? 1 : -1
            wheelAccum -= CGFloat(d)
            editor.adjustThickness(d)
        }
    }

    override func cursorUpdate(with event: NSEvent) { updateHover(local(event)) }

    private func updateHover(_ p: CGPoint) {
        if closing { return }
        let px = pixel(p)
        if editor.pipetteOn { updateMagnifier(px); NSCursor.crosshair.set(); return }
        if mode == .selecting && !selection.hasSelection {
            let h = WindowInfo.topmost(shotInfo.windows, at: px)?.rect ?? monitorPixels
            if h != hint { hint = h; canvas.needsDisplay = true }
            updateMagnifier(px)
            NSCursor.crosshair.set()
            return
        }
        let handle = selection.hitTest(px, tolerance: handleTolerance)
        if mode == .editing && handle == .move {
            if toolbar.currentTool == .select { (document.hitTest(exact(p)) != nil ? NSCursor.pointingHand : NSCursor.openHand).set() }
            else { editor.cursor(for: toolbar.currentTool).set() }
            return
        }
        cursor(for: handle).set()
    }

    private func cursor(for h: Handle) -> NSCursor {
        switch h {
        case .topLeft, .bottomRight:
            if #available(macOS 15.0, *) { return .frameResize(position: h == .topLeft ? .topLeft : .bottomRight, directions: .all) }
            return .crosshair
        case .topRight, .bottomLeft:
            if #available(macOS 15.0, *) { return .frameResize(position: h == .topRight ? .topRight : .bottomLeft, directions: .all) }
            return .crosshair
        case .top, .bottom: return .resizeUpDown
        case .left, .right: return .resizeLeftRight
        case .move: return .openHand
        case .none: return .arrow
        }
    }

    // MARK: Snapping the frame (OverlayWindow.Snap.cs)

    private var snapActive: Bool { settings.snapEnabled && !NSEvent.modifierFlags.contains(.option) }

    private func snapFrameEdge(_ axis: EdgeAxis, _ edge: Int, _ cross: Int) -> Int? {
        let threshold = Int((8 * pixelScale).rounded())
        var best: Int?
        func consider(_ c: Int) { if abs(c - edge) <= threshold && (best == nil || abs(c - edge) < abs(best! - edge)) { best = c } }
        for e in WindowInfo.visibleEdges(shotInfo.windows, axis, cross) { consider(e) }
        if edgeSnapper == nil { edgeSnapper = EdgeSnapper(shot) }
        if let f = edgeSnapper?.find(axis, edge: edge, cross: cross, threshold: threshold) { consider(f) }
        if let b = best {
            frameGuides.append(axis == .vertical
                ? SnapGuide(orientation: .vertical, position: Double(b), start: 0, end: Double(shot.height))
                : SnapGuide(orientation: .horizontal, position: Double(b), start: 0, end: Double(shot.width)))
        }
        return best
    }

    override func flagsChanged(with e: NSEvent) {
        // Option toggles snapping mid-gesture without moving the mouse.
        guard !closing, !textMode else { return }
        let p = mouseLocal
        if editor.toolGestureActive { editor.refreshGesture(toolPoint(p)); return }
        if draggingHandle { frameGuides = []; selection.drag(to: pixel(p), snap: snapActive ? snapFrameEdge : nil); refreshSelectionVisuals() }
        else if let a = dragAnchor, thresholdPassed {
            frameGuides = []
            selection.rect = SelectionModel.fromPoints(a, pixel(p), snap: snapActive ? snapFrameEdge : nil)
            refreshSelectionVisuals()
        }
    }

    // MARK: Modes

    private func enterEditing() {
        mode = .editing
        hint = nil
        magnifier.isHidden = true
        hidePipetteBadge()
        toolbar.isHidden = false
        editor.refreshOptions()
        refreshSelectionVisuals()
        updateHover(mouseLocal)
    }

    /// Resets the current mouse gesture: live shape and drags roll back, a new frame is dropped. Idempotent.
    func resetInteraction() {
        guard hasGesture else { return }
        if textPress != nil { textPress = nil; textDragging = false; showTextSelection(); return }
        editor.cancelGesture()
        if draggingHandle { draggingHandle = false; selection.endDrag() }
        if dragAnchor != nil { dragAnchor = nil; if mode == .selecting { selection.rect = .empty } }
        thresholdPassed = false
        frameGuides = []
        if mode == .selecting && selection.hasSelection { enterEditing() }
        refreshSelectionVisuals()
    }

    /// Esc and right click peel one layer: gesture → eyedropper/text mode → text input → ruler → selected shape or
    /// drawing tool → the selection (markup stays) → close.
    func stepBack() {
        if hasGesture { resetInteraction(); return }
        if editor.pipetteOn { editor.setPipette(false); return }
        if textMode {
            if closeCodePopup() { return }
            if !textSelection.isEmpty { textSelection = []; textAnchor = nil; showTextSelection() } else { exitTextMode() }
            return
        }
        if editor.isTextEditing { editor.commitTextEdit(); return }
        if editor.ruler.measurement != nil { editor.ruler.clear(); return }
        if document.selected != nil || (mode == .editing && toolbar.currentTool != .select) {
            editor.deselect(); editor.setTool(.select); return
        }
        if selection.hasSelection { resetSelection(); return }
        cancel()
    }

    /// Back to choosing an area. Markup stays in screenshot coordinates.
    func resetSelection() {
        editor.commitTextEdit()
        resetInteraction()
        editor.setPipette(false)
        editor.ruler.clear()
        mode = .selecting
        selection.rect = .empty
        editor.deselect()
        toolbar.isHidden = true
        editor.refreshOptions()
        hideHint()
        refreshSelectionVisuals()
        updateHover(mouseLocal)
    }

    // MARK: HUD

    func refreshSelectionVisuals() {
        canvas.needsDisplay = true
        guard selection.hasSelection else { sizeLabel.isHidden = true; return }
        let r = selection.rect
        sizeLabel.text = "\(r.width) × \(r.height)"
        sizeLabel.trailingIcon = editor.beautifyOn && !textMode ? (.sparkle, RGBA(hex: 0xFFD66B).nsColor) : nil
        sizeLabel.resize()
        sizeLabel.isHidden = false
        let pt = toPoints(r.cgRect)
        var top = pt.minY - sizeLabel.frame.height - 6
        if top < 0 { top = pt.minY + 6 }
        sizeLabel.setFrameOrigin(CGPoint(x: pt.minX, y: top))
        if !toolbar.isHidden { positionToolbar() }
        if !textHint.isHidden { positionTextHint() }
    }

    private func positionToolbar() {
        toolbar.layoutButtons()
        let (p, below) = HudLayout.place(selection: toPoints(selection.rect.cgRect), work: workArea, toolbar: toolbar.frame.size)
        toolbar.setFrameOrigin(p)
        toolbarBelow = below
        editor.flyouts.reposition()
    }

    private func updateMagnifier(_ p: PixelPoint) {
        magnifier.isHidden = false
        magnifier.showColor = settings.isOn(.pipette)
        magnifier.update(shot, p)
        let c = CGPoint(x: CGFloat(p.x) / pixelScale, y: CGFloat(p.y) / pixelScale)
        let size = magnifier.frame.size, off: CGFloat = 20
        var x = c.x + off, y = c.y + off
        if x + size.width > bounds.maxX { x = c.x - off - size.width }
        if y + size.height > bounds.maxY { y = c.y - off - size.height }
        magnifier.setFrameOrigin(CGPoint(x: x, y: y))
        positionPipetteBadge()
    }

    private func copyColorUnderMagnifier() {
        guard settings.isOn(.pipette) else { return }
        let p = magnifier.current ?? pixel(mouseLocal)
        let c = shot.color(at: p).opaque
        NSPasteboard.general.clearContents()
        let ok = NSPasteboard.general.setString(c.rgbHex, forType: .string)
        pipetteBadge.chip = c
        pipetteBadge.text = ok ? Loc.f("Pipette.Copied", c.rgbHex) : Loc.t("Common.ClipboardBusy")
        pipetteBadge.isHidden = false
        positionPipetteBadge()
        badgeTimer?.invalidate()
        badgeTimer = Timer.scheduledTimer(withTimeInterval: 1.6, repeats: false) { [weak self] _ in self?.hidePipetteBadge() }
    }

    private func hidePipetteBadge() { badgeTimer?.invalidate(); pipetteBadge.isHidden = true }

    private func positionPipetteBadge() {
        guard !pipetteBadge.isHidden, !magnifier.isHidden else { return }
        let m = magnifier.frame
        let below = m.maxY + 4
        pipetteBadge.setFrameOrigin(CGPoint(x: m.minX, y: below + pipetteBadge.frame.height <= bounds.maxY ? below : m.minY - 4 - pipetteBadge.frame.height))
    }

    private func showHint(_ text: String, hideAfter: Double? = nil) {
        hintTimer?.invalidate()
        textHint.text = text
        textHint.isHidden = false
        positionTextHint()
        if let after = hideAfter {
            hintTimer = Timer.scheduledTimer(withTimeInterval: after, repeats: false) { [weak self] _ in
                guard let self else { return }
                if !self.textMode { self.hideHint() } else if self.ocr != nil { self.showHint(self.hintFor(self.textSelection)) }
            }
        }
    }

    private func hideHint() { hintTimer?.invalidate(); textHint.isHidden = true }

    private func positionTextHint() {
        guard selection.hasSelection else { return }
        var x = sizeLabel.frame.maxX + 6, y = sizeLabel.frame.minY
        if x + textHint.frame.width > bounds.maxX - 8 {
            x = max(8, bounds.maxX - 8 - textHint.frame.width)
            y += sizeLabel.frame.height + 6
        }
        textHint.setFrameOrigin(CGPoint(x: x, y: y - (textHint.frame.height - sizeLabel.frame.height) / 2))
    }

    // MARK: Keyboard

    override func keyDown(with e: NSEvent) {
        if editor.isTextEditing { return }
        let cmd = e.modifierFlags.contains(.command), shift = e.modifierFlags.contains(.shift)
        if textMode { if !textKeyDown(e) { super.keyDown(with: e) }; return }
        switch e.keyCode {
        case Key.esc: stepBack()
        case Key.ret, Key.enter:
            if mode == .selecting && !selection.hasSelection && !hasGesture { selectHintUnderCursor() }
            complete(.copy)
        case Key.c where cmd: complete(.copyExplicit)
        case Key.c where mode == .selecting && !magnifier.isHidden && settings.isOn(.pipette): copyColorUnderMagnifier()
        case Key.s where cmd: saveAs()
        case Key.f3 where settings.actionAllowed(.pin): complete(.pin)
        case Key.t where cmd && settings.isOn(.ocr): toggleTextMode()
        case Key.left, Key.right, Key.up, Key.down where selection.hasSelection && document.selected == nil:
            if hasGesture { return }
            let step = e.modifierFlags.contains(.option) || e.modifierFlags.contains(.command) ? 10 : 1
            let k = e.keyCode
            let dx = k == Key.left ? -step : k == Key.right ? step : 0
            let dy = k == Key.up ? -step : k == Key.down ? step : 0
            if shift { selection.resize(dx, dy) } else { selection.nudge(dx, dy) }
            refreshSelectionVisuals()
        default:
            if !editor.handleKey(e, editing: mode == .editing) { super.keyDown(with: e) }
        }
    }

    override func performKeyEquivalent(with e: NSEvent) -> Bool {
        guard window?.firstResponder === self, e.modifierFlags.contains(.command) else { return super.performKeyEquivalent(with: e) }
        keyDown(with: e)
        return true
    }

    private func selectHintUnderCursor() {
        let p = pixel(mouseLocal)
        selection.rect = (hint ?? WindowInfo.topmost(shotInfo.windows, at: p)?.rect ?? monitorPixels).clamped(to: shot.bounds)
    }

    // MARK: Finishing

    private func saveAs() {
        if closing { return }
        editor.commitTextEdit()
        resetInteraction()
        guard selection.hasSelection, let window else { return }
        let panel = NSSavePanel()
        panel.title = Loc.t("Common.SaveScreenshotTitle")
        panel.allowedContentTypes = [.png, .jpeg]
        let f = DateFormatter(); f.dateFormat = "yyyyMMdd_HHmmss"
        panel.nameFieldStringValue = "Screeny_\(f.string(from: Date())).png"
        panel.directoryURL = FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask).first
        panel.isExtensionHidden = false
        panel.level = NSWindow.Level(rawValue: window.level.rawValue + 1)
        let format = SaveFormatAccessory(panel: panel)
        panel.accessoryView = format.view
        dialogOpen = true
        let response = panel.runModal()
        dialogOpen = false
        window.makeKeyAndOrderFront(nil)
        window.makeFirstResponder(self)
        guard response == .OK, let url = panel.url else { return }
        complete(.save, saveURL: url)
    }

    func complete(_ action: CaptureAction, saveURL: URL? = nil) {
        if closing || !settings.actionAllowed(action) { return }
        editor.commitTextEdit()
        resetInteraction()
        guard selection.hasSelection else { return }
        let region = selection.rect
        document.selected = nil
        var image = ImageComposer.compose(shot, region: region, doc: document)
        if editor.beautifyOn { image = BeautifyComposer.compose(image, editor.beautify) }
        let json = AnnotationSerializer.serialize(document.items, regionOrigin: region.cgRect.origin, width: region.width, height: region.height,
                                                  beautify: editor.beautifyOn ? editor.beautify : nil)
        let original = settings.keepOriginals && !document.items.isEmpty ? shot.cropped(region).cgImage : nil
        closing = true
        finished?(CaptureResult(image: image, action: action, region: region, screenRect: screenRect(region), scale: pixelScale,
                                saveURL: saveURL, text: nil, annotationsJSON: json, original: original))
    }

    func cancel() {
        if closing { return }
        closing = true
        resetInteraction()
        finished?(nil)
    }

    private func screenRect(_ r: PixelRect) -> CGRect {
        let f = shotInfo.screen.frame
        let pt = toPoints(r.cgRect)
        return CGRect(x: f.minX + pt.minX, y: f.maxY - pt.maxY, width: pt.width, height: pt.height)
    }

    // MARK: Text mode (⌘T)

    private func toggleTextMode() {
        if textMode { exitTextMode(); return }
        if closing || hasGesture || !settings.isOn(.ocr) { return }
        editor.commitTextEdit()
        if mode == .selecting {
            if !selection.hasSelection { selectHintUnderCursor() }
            if !selection.hasSelection { return }
            enterEditing()
        }
        enterTextMode()
    }

    private func enterTextMode() {
        editor.deselect()
        editor.setPipette(false)
        editor.ruler.clear()
        textMode = true
        editor.refreshOptions()
        ocrGeneration += 1
        let generation = ocrGeneration
        ocr = nil
        textSelection = []; textAnchor = nil
        toolbar.setTextMode(true)
        refreshSelectionVisuals()
        positionToolbar()
        showHint(Loc.t("TextMode.Recognizing"))
        let region = selection.rect
        // Recognize what would be copied (without the frame): blurred text doesn't turn back into text.
        let image = ImageComposer.compose(shot, region: region, doc: document)
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            var layout: OcrLayout?
            var failure: String?
            do { layout = try OcrService.recognize(image) } catch { failure = Loc.f("TextMode.Failed", error.localizedDescription) }
            let found = OcrService.scanCodes(image)
            DispatchQueue.main.async {
                guard let self, generation == self.ocrGeneration, !self.closing, self.textMode else { return }
                let offset = CGVector(dx: region.x, dy: region.y)
                let mapped = found.map { DetectedCode(format: $0.format, text: $0.text, box: $0.box.offset(offset)) }
                if layout?.isEmpty ?? true {
                    if mapped.isEmpty { self.failTextMode(failure ?? Loc.t("TextMode.NotFound")); return }
                    self.showTextLayout(.empty, region)
                    self.showCodes(mapped)
                    self.showHint(self.hintFor(self.textSelection))
                    if self.copyWhenReady { self.copyText() }
                    return
                }
                self.showTextLayout(layout!.map(offset: offset), region)
                if self.copyWhenReady { self.copyText(); return }
                if !mapped.isEmpty { self.showCodes(mapped); self.showHint(self.hintFor(self.textSelection)) }
            }
        }
    }

    private func showTextLayout(_ l: OcrLayout, _ region: PixelRect) {
        ocr = l
        textLayer = TextLayerState(layout: l, area: region.cgRect)
        canvas.needsDisplay = true
        showHint(hintFor(textSelection))
        updateTextCursor(mouseLocal)
    }

    private func failTextMode(_ message: String) {
        exitTextMode()
        showHint(message, hideAfter: message.count > 30 ? 4 : 1.8)
    }

    private func exitTextMode() {
        guard textMode else { return }
        textMode = false
        ocrGeneration += 1
        ocr = nil
        textSelection = []; textAnchor = nil; textPress = nil; textDragging = false; copyWhenReady = false
        codes = []; openCode = nil; codePopup.isHidden = true
        textLayer = nil
        toolbar.setTextMode(false)
        refreshSelectionVisuals()
        positionToolbar()
        hideHint()
        editor.refreshOptions()
        updateHover(mouseLocal)
    }

    private func hintFor(_ sel: Set<Int>) -> String {
        if ocr?.isEmpty == true && !codes.isEmpty { return Loc.t(codes.count == 1 ? "TextMode.OneCode" : "TextMode.ManyCodes") }
        return Loc.t(!sel.isEmpty ? "TextMode.HintSelection" : !codes.isEmpty ? "TextMode.HintNoSelectionCodes" : "TextMode.HintNoSelection")
    }

    private func showTextSelection(_ marquee: CGRect? = nil) {
        textLayer?.selection = textSelection
        textLayer?.marquee = marquee
        canvas.needsDisplay = true
        if ocr != nil { showHint(hintFor(textSelection)) }
    }

    private func textMouseDown(_ e: NSEvent, _ pt: CGPoint) {
        guard let ocr else { return }
        if e.clickCount == 1, codeMouseDown(pt) { return }
        let p = exact(pt)
        let cmd = e.modifierFlags.contains(.command), shift = e.modifierFlags.contains(.shift)
        let word = ocr.wordAt(p, tolerance: 3 * pixelScale)
        if e.clickCount >= 2, let w = word {
            textSelection = cmd ? textBase.union(ocr.lineRange(w)) : ocr.lineRange(w)
            textAnchor = w
            showTextSelection()
            return
        }
        let extend = shift && textAnchor != nil
        if extend, let w = word { textSelection = ocr.range(textAnchor!, w); showTextSelection() }
        textBase = cmd ? textSelection : []
        textPress = p
        textDragFromWord = word != nil || extend
        textDragging = extend
        if let w = word, !extend { textAnchor = w }
    }

    private func textMouseMove(_ pt: CGPoint, dragging: Bool) {
        let p = exact(pt)
        guard dragging, let start = textPress, let ocr else { updateTextCursor(pt); return }
        let t = CGFloat(Self.dragThreshold) * pixelScale
        if !textDragging && abs(p.x - start.x) < t && abs(p.y - start.y) < t { return }
        textDragging = true
        if textDragFromWord, let anchor = textAnchor {
            let target = ocr.wordAt(p, tolerance: 3 * pixelScale) ?? nearestWord(p) ?? anchor
            textSelection = textBase.union(ocr.range(anchor, target))
            showTextSelection()
        } else {
            let area = CGRect(corner: start, p).intersection(selection.rect.cgRect)
            textSelection = textBase.union(area.isNull ? [] : ocr.inRect(area))
            showTextSelection(area.isNull ? nil : area)
        }
    }

    private func textMouseUp(_ e: NSEvent, _ pt: CGPoint) {
        guard textPress != nil, let ocr else { return }
        let click = !textDragging
        textPress = nil; textDragging = false
        if click {
            let cmd = e.modifierFlags.contains(.command)
            if let w = ocr.wordAt(exact(pt), tolerance: 3 * pixelScale) {
                var set = textBase
                if !set.insert(w).inserted && cmd { set.remove(w) }
                textSelection = set
            } else if !cmd { textSelection = []; textAnchor = nil }
        }
        showTextSelection()
    }

    private func nearestWord(_ p: CGPoint) -> Int? {
        guard let ocr, !ocr.isEmpty else { return nil }
        if p.y > OcrLayout.lineBox(ocr.lines.last!).maxY { return ocr.wordCount - 1 }
        if p.y < OcrLayout.lineBox(ocr.lines[0]).minY { return 0 }
        var best = 0, bestD = Double.greatestFiniteMagnitude
        for i in 0..<ocr.wordCount {
            let b = ocr.word(i).box
            let dx = max(0, max(b.minX - p.x, p.x - b.maxX)), dy = max(0, max(b.minY - p.y, p.y - b.maxY))
            let d = dx * dx + dy * dy * 4
            if d < bestD { bestD = d; best = i }
        }
        return best
    }

    private func updateTextCursor(_ pt: CGPoint) {
        guard let ocr else { NSCursor.arrow.set(); return }
        let p = exact(pt)
        if !selection.rect.cgRect.contains(p) { NSCursor.arrow.set() }
        else if codeAt(p) != nil { NSCursor.pointingHand.set() }
        else if ocr.wordAt(p, tolerance: 3 * pixelScale) != nil { NSCursor.iBeam.set() }
        else { NSCursor.crosshair.set() }
    }

    private func textKeyDown(_ e: NSEvent) -> Bool {
        let cmd = e.modifierFlags.contains(.command)
        switch e.keyCode {
        case Key.esc: stepBack()
        case Key.t where cmd: exitTextMode()
        case Key.a where cmd:
            if let ocr, !ocr.isEmpty { textSelection = ocr.all(); textAnchor = 0; showTextSelection() }
        case Key.ret, Key.enter: copyText()
        case Key.c where cmd: copyText()
        case Key.f3 where settings.actionAllowed(.pin): exitTextMode(); complete(.pin)
        case Key.s where cmd: saveAs()
        default: break
        }
        return true
    }

    private func copyText() {
        guard !closing, textMode else { return }
        guard let ocr else { copyWhenReady = true; showHint(Loc.t("TextMode.CopyWhenReady")); return }
        var text = ocr.textOf(textSelection)
        if ocr.isEmpty, let i = openCode ?? (codes.count == 1 ? 0 : nil) { text = codes[i].text }
        else if ocr.isEmpty && codes.count > 1 { showHint(Loc.t("TextMode.ClickCode")); return }
        if text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty { showHint(Loc.t("TextMode.NoTextInSelection")); return }
        closing = true
        finished?(CaptureResult(image: shot.cgImage, action: .copyText, region: selection.rect, screenRect: screenRect(selection.rect),
                                scale: pixelScale, saveURL: nil, text: text, annotationsJSON: nil, original: nil))
    }

    // Codes (OverlayWindow.Codes.cs)

    private func showCodes(_ c: [DetectedCode]) {
        codes = c
        textLayer?.codes = c.map(\.box)
        canvas.needsDisplay = true
    }

    private func codeAt(_ p: CGPoint) -> Int? { codes.firstIndex { $0.box.contains(p) } }

    private func codeMouseDown(_ pt: CGPoint) -> Bool {
        guard let i = codeAt(exact(pt)) else { return false }
        openCode = i
        textLayer?.activeCode = i
        canvas.needsDisplay = true
        let c = codes[i]
        codePopup.show(format: c.isLink ? Loc.f("Codes.FormatLink", c.format) : c.format, text: c.text, link: c.isLink)
        let box = toPoints(c.box)
        var x = box.maxX + 8
        if x + codePopup.frame.width > bounds.maxX - 8 { x = max(8, box.minX - 8 - codePopup.frame.width) }
        let y = clampInto(box.minY, 8, bounds.maxY - codePopup.frame.height - 8)
        codePopup.setFrameOrigin(CGPoint(x: x, y: y))
        codePopup.isHidden = false
        return true
    }

    @discardableResult private func closeCodePopup() -> Bool {
        guard !codePopup.isHidden else { return false }
        codePopup.isHidden = true
        openCode = nil
        textLayer?.activeCode = nil
        canvas.needsDisplay = true
        return true
    }

    private func copyOpenCode() {
        guard let i = openCode else { return }
        NSPasteboard.general.clearContents()
        NSPasteboard.general.setString(codes[i].text, forType: .string)
        closeCodePopup()
        showHint(Loc.t("Codes.Copied"), hideAfter: 1.6)
    }

    private func openCodeLink() {
        guard let i = openCode, codes[i].isLink, let url = URL(string: codes[i].text.trimmingCharacters(in: .whitespacesAndNewlines)) else { return }
        if !NSWorkspace.shared.open(url) { showHint(Loc.t("Codes.OpenFailed"), hideAfter: 2) } else { cancel() }
    }

    // MARK: Dev/QA

    /// Selects an area and enters drawing mode as if dragged with the mouse (QA scenarios).
    func devSelect(_ r: PixelRect) {
        selection.rect = r.clamped(to: shot.bounds)
        if selection.hasSelection { enterEditing() }
    }
    func devSetHint(_ r: PixelRect?) { hint = r; canvas.needsDisplay = true }
    func devMagnifier(at p: PixelPoint) { updateMagnifier(p) }
    func devEnterTextMode() { toggleTextMode() }
    var devTextReady: Bool { ocr != nil }
    func devSelectAllText() { if let ocr { textSelection = ocr.all(); showTextSelection() } }
}

/// QR / barcode contents popup in text mode; links open only by the button.
final class CodePopupView: HudCard {
    private let formatLabel = NSTextField(labelWithString: "")
    private let textLabel = NSTextField(wrappingLabelWithString: "")
    private let openButton = stripButton(title: "", width: 60)
    private let copyButton = stripButton(title: "", width: 60)
    var copyAction: (() -> Void)?
    var openAction: (() -> Void)?

    init() {
        super.init(frame: CGRect(x: 0, y: 0, width: 340, height: 120))
        radius = 10
        formatLabel.font = .systemFont(ofSize: 11); formatLabel.textColor = Palette.overlayInk70.nsColor
        textLabel.font = .monospacedSystemFont(ofSize: 12.5, weight: .regular); textLabel.textColor = Palette.overlayInk.nsColor
        textLabel.maximumNumberOfLines = 7
        openButton.action = { [weak self] in self?.openAction?() }
        copyButton.action = { [weak self] in self?.copyAction?() }
        copyButton.accent = true
        for b in [openButton, copyButton] { b.size = CGSize(width: 60, height: 28); b.cornerRadius = 6 }
        openButton.wantsLayer = true
        for v in [formatLabel, textLabel, openButton, copyButton] as [NSView] { addSubview(v) }
    }
    required init?(coder: NSCoder) { fatalError() }

    func show(format: String, text: String, link: Bool) {
        formatLabel.stringValue = format
        textLabel.stringValue = text
        openButton.title = Loc.t("Common.Open")
        copyButton.title = Loc.t("Common.Copy")
        openButton.isHidden = !link
        let w: CGFloat = 340
        formatLabel.frame = CGRect(x: 12, y: 10, width: w - 24, height: 16)
        let th = min(132, ceil(textLabel.sizeThatFits(CGSize(width: w - 24, height: 1000)).height))
        textLabel.frame = CGRect(x: 12, y: 30, width: w - 24, height: th)
        let by = 30 + th + 10
        let cw = copyButton.intrinsicContentSize.width, ow = openButton.intrinsicContentSize.width
        copyButton.frame = CGRect(x: w - 12 - cw, y: by, width: cw, height: 28)
        openButton.frame = CGRect(x: w - 12 - cw - 6 - ow, y: by, width: ow, height: 28)
        setFrameSize(CGSize(width: w, height: by + 28 + 10))
        needsDisplay = true
    }
}

/// PNG/JPEG choice in the save panel (the Windows filter list).
final class SaveFormatAccessory: NSObject {
    let view: NSView
    private let popup = NSPopUpButton()
    private weak var panel: NSSavePanel?

    init(panel: NSSavePanel) {
        self.panel = panel
        view = NSView(frame: CGRect(x: 0, y: 0, width: 220, height: 36))
        super.init()
        popup.addItems(withTitles: ["PNG", "JPEG"])
        popup.frame = CGRect(x: 60, y: 6, width: 120, height: 26)
        popup.target = self
        popup.action = #selector(changed)
        view.addSubview(popup)
        objc_setAssociatedObject(panel, "screenyFormat", self, .OBJC_ASSOCIATION_RETAIN)
    }

    @objc private func changed() {
        guard let panel else { return }
        let ext = popup.indexOfSelectedItem == 1 ? "jpg" : "png"
        let base = (panel.nameFieldStringValue as NSString).deletingPathExtension
        panel.allowedContentTypes = [popup.indexOfSelectedItem == 1 ? .jpeg : .png]
        panel.nameFieldStringValue = "\(base).\(ext)"
    }
}

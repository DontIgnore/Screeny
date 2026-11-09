import AppKit
import UniformTypeIdentifiers

/// A history entry opened for re-editing (Editing/EditSession.cs): the clean original with its markup when kept,
/// otherwise the finished picture flat, with a message why.
struct EditSession {
    let url: URL
    let base: PixelImage
    let hasOriginal: Bool
    let originalPng: Data?
    let annotations: [Annotation]
    let beautify: BeautifyOptions?
    let message: String?

    static func load(_ item: HistoryItem) -> EditSession? {
        guard let finished = PixelImage.load(url: item.url) else { return nil }
        let (jsonURL, origURL) = HistoryStore.sidecars(item.url)
        guard FileManager.default.fileExists(atPath: origURL.path) else {
            return EditSession(url: item.url, base: finished, hasOriginal: false, originalPng: nil, annotations: [], beautify: nil, message: nil)
        }
        var problem: String
        do {
            let data = try Data(contentsOf: origURL)
            guard let src = CGImageSourceCreateWithData(data as CFData, nil), let cg = CGImageSourceCreateImageAtIndex(src, 0, nil) else {
                throw CocoaError(.fileReadCorruptFile)
            }
            let original = PixelImage(cgImage: cg)
            if !FileManager.default.fileExists(atPath: jsonURL.path) { problem = Loc.t("Edit.NoDocument") } else {
                let file = try AnnotationSerializer.deserialize(String(contentsOf: jsonURL, encoding: .utf8))
                if file.width != original.width || file.height != original.height {
                    problem = Loc.f("Edit.SizeMismatch", file.width, file.height, original.width, original.height)
                } else if file.skipped > 0 {
                    problem = Loc.f("Edit.UnknownMarks", file.skipped)
                } else {
                    return EditSession(url: item.url, base: original, hasOriginal: true, originalPng: data, annotations: file.annotations,
                                       beautify: file.beautify, message: nil)
                }
            }
        } catch DocumentError.unsupportedVersion(let v) {
            problem = Loc.f("Edit.NewerDocument", v)
        } catch is DocumentError {
            problem = Loc.t("Edit.DocumentDamaged")
        } catch {
            Log.error("Re-edit sidecar read failed: \(item.url.path)", error)
            problem = Loc.t("Edit.ReadOriginalFailed")
        }
        return EditSession(url: item.url, base: finished, hasOriginal: false, originalPng: nil, annotations: [], beautify: nil,
                           message: Loc.f("Edit.OpenedFlat", problem))
    }
}

/// Re-editing window for a history entry (Editing/EditorWindow.xaml): same toolbar and tools as the overlay,
/// zoomable canvas, "Original / With markup" switch, every action adds a new history entry.
final class EditorWindowController: NSWindowController, NSWindowDelegate {
    let session: EditSession
    unowned let services: AppServices
    private let canvas: EditorCanvasView
    private let modeSwitch = NSSegmentedControl()
    private let clearButton = ThemeButton("", kind: .subtle)
    private var savedRevision = 0
    private var closingNow = false
    var closed: (() -> Void)?

    init?(item: HistoryItem, services: AppServices) {
        guard let s = EditSession.load(item) else { return nil }
        session = s
        self.services = services
        let w = NSWindow(contentRect: CGRect(x: 0, y: 0, width: 1180, height: 800), styleMask: [.titled, .closable, .resizable, .miniaturizable],
                         backing: .buffered, defer: false)
        w.minSize = CGSize(width: 760, height: 480)
        Theme.style(w)
        canvas = EditorCanvasView(session: s, settings: services.settings)
        super.init(window: w)
        w.delegate = self
        let root = FlippedView()
        root.wantsLayer = true
        w.contentView = root
        let bar = FlippedView()
        root.addSubview(bar)
        root.addSubview(canvas)
        bar.addSubview(modeSwitch)
        bar.addSubview(clearButton)
        modeSwitch.segmentCount = 2
        modeSwitch.trackingMode = .selectOne
        modeSwitch.selectedSegment = 1
        modeSwitch.target = self
        modeSwitch.action = #selector(modeChanged)
        modeSwitch.isHidden = !s.hasOriginal
        clearButton.handler = { [weak self] in self?.clearMarkup() }
        canvas.restartRequested = { [weak self] in
            self?.canvas.editor.document.removeAll()
            self?.modeSwitch.selectedSegment = 1
            self?.modeChanged()
        }
        canvas.actionRequested = { [weak self] a in self?.run(a) }
        canvas.closeRequested = { [weak self] in self?.window?.performClose(nil) }
        canvas.documentChanged = { [weak self] in self?.updateClear() }
        root.postsFrameChangedNotifications = true
        NotificationCenter.default.addObserver(self, selector: #selector(layoutRoot), name: NSView.frameDidChangeNotification, object: root)
        NotificationCenter.default.addObserver(self, selector: #selector(relocalize), name: Loc.changed, object: nil)
        savedRevision = canvas.editor.document.undo.revision
        relocalize()
        layoutRoot()
        w.center()
        w.setFrameAutosaveName("EditorWindow")
        DispatchQueue.main.async { w.makeFirstResponder(self.canvas) }
    }
    required init?(coder: NSCoder) { fatalError() }

    @objc private func layoutRoot() {
        guard let root = window?.contentView else { return }
        let top: CGFloat = 32 // title bar
        let barH: CGFloat = 40
        let bar = root.subviews[0]
        bar.frame = CGRect(x: 0, y: top, width: root.bounds.width, height: barH)
        canvas.frame = CGRect(x: 0, y: top + barH, width: root.bounds.width, height: root.bounds.height - top - barH)
        modeSwitch.sizeToFit()
        let cw = clearButton.intrinsicContentSize.width
        clearButton.frame = CGRect(x: bar.bounds.width - 12 - cw, y: 4, width: cw, height: 30)
        modeSwitch.frame.origin = CGPoint(x: clearButton.frame.minX - 10 - modeSwitch.frame.width, y: 7)
    }

    @objc private func relocalize() {
        modeSwitch.setLabel(Loc.t("Editor.Original"), forSegment: 0)
        modeSwitch.setLabel(Loc.t("Editor.WithMarkup"), forSegment: 1)
        modeSwitch.setToolTip(Loc.t("Editor.OriginalTip"), forSegment: 0)
        clearButton.title = Loc.t("Editor.Clear")
        clearButton.toolTip = Loc.t("Editor.ClearTip")
        let name = session.url.lastPathComponent
        if let (d, _) = HistoryStore.parseName(name) {
            window?.title = Loc.f("Editor.TitleShot", DateFormatter.localizedString(from: d, dateStyle: .short, timeStyle: .medium))
        } else { window?.title = Loc.f("Editor.TitleFile", name) }
        canvas.updateNotice()
        layoutRoot()
        updateClear()
    }

    func applyFeatures() { canvas.editor.applyFeatures(); canvas.relayoutToolbar() }

    @objc private func modeChanged() {
        canvas.setOriginalMode(modeSwitch.selectedSegment == 0)
        updateClear()
    }

    private func clearMarkup() {
        guard !canvas.originalMode else { return }
        canvas.editor.commitTextEdit()
        canvas.editor.document.removeAll()
    }

    private func updateClear() { clearButton.isEnabled = !canvas.originalMode && !canvas.editor.document.items.isEmpty }

    private var dirty: Bool { canvas.editor.document.undo.revision != savedRevision }

    // MARK: Actions: each adds a new history entry; the window stays open.

    private func run(_ action: CaptureAction, saveURL: URL? = nil) {
        let settings = services.settings
        guard settings.actionAllowed(action) else { return }
        if action == .save && saveURL == nil { saveAs(); return }
        let ed: AnnotationEditor = canvas.editor
        ed.commitTextEdit()
        canvas.resetInteraction()
        ed.deselect()
        let b = session.base
        let region = b.bounds
        let visible = canvas.originalMode ? [] : ed.document.items.filter { $0.bounds.intersects(region.cgRect) }
        let doc = AnnotationDocument(); doc.load(visible)
        var image = ImageComposer.compose(b, region: region, doc: doc)
        let beautify = ed.beautifyOn ? ed.beautify : nil
        if let o = beautify { image = BeautifyComposer.compose(image, o) }
        let json = AnnotationSerializer.serialize(visible, regionOrigin: .zero, width: region.width, height: region.height, beautify: beautify)
        let original = settings.keepOriginals && session.hasOriginal ? session.originalPng : nil
        let revision = ed.document.undo.revision
        let originalMode = canvas.originalMode
        canvas.toast(Loc.t("Editor.Saving"))
        var done: String
        switch action {
        case .copyExplicit, .copy: done = Loc.t(services.copyImage(image, scale: 1) ? "Common.Copied" : "Common.ClipboardBusy")
        case .pin: services.pin(image, scale: 1); done = Loc.t("Editor.Pinned")
        default: done = Loc.t("Editor.Saved")
        }
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            guard let self else { return }
            var historyKey: String?
            var message = done
            do {
                guard let png = PixelImage.encode(image, type: .png) else { throw CocoaError(.fileWriteUnknown) }
                if settings.isOn(.history) {
                    do {
                        try self.services.history.save(png: png, json: json, originalPng: original)
                        historyKey = "Editor.ToastNewEntry"
                        DispatchQueue.main.async {
                            NotificationCenter.default.post(name: self.services.historyChanged, object: nil)
                            if !originalMode { self.savedRevision = revision }
                        }
                    } catch { Log.error("Editor history save failed", error); historyKey = "Editor.ToastHistoryFailed" }
                }
                if action == .save, let url = saveURL {
                    let data = url.pathExtension.lowercased().hasPrefix("jp") ? PixelImage.encode(image, type: .jpeg) : png
                    try data?.write(to: url, options: .atomic)
                }
            } catch {
                Log.error("Editor action failed", error)
                message = Loc.f("Editor.Failed", error.localizedDescription)
            }
            let text = historyKey.map { Loc.f($0, message) } ?? message
            DispatchQueue.main.async { if !self.closingNow { self.canvas.toast(text) } }
        }
    }

    private func saveAs() {
        guard let window else { return }
        let panel = NSSavePanel()
        panel.title = Loc.t("Common.SaveScreenshotTitle")
        panel.allowedContentTypes = [.png, .jpeg]
        let f = DateFormatter(); f.dateFormat = "yyyyMMdd_HHmmss"
        panel.nameFieldStringValue = "Screeny_\(f.string(from: Date())).png"
        panel.directoryURL = FileManager.default.urls(for: .picturesDirectory, in: .userDomainMask).first
        let acc = SaveFormatAccessory(panel: panel)
        panel.accessoryView = acc.view
        panel.beginSheetModal(for: window) { [weak self] r in
            if r == .OK, let url = panel.url { self?.run(.save, saveURL: url) }
        }
    }

    func windowShouldClose(_ sender: NSWindow) -> Bool {
        canvas.editor.commitTextEdit()
        if dirty {
            let a = NSAlert()
            a.messageText = Loc.t("Editor.CloseTitle")
            let s = services.settings
            let pins = s.actionAllowed(.pin)
            a.informativeText = Loc.t(s.isOn(.history) ? (pins ? "Editor.CloseHistoryPins" : "Editor.CloseHistory") : (pins ? "Editor.ClosePins" : "Editor.CloseLost"))
            a.addButton(withTitle: Loc.t("Common.Close")).hasDestructiveAction = true
            a.addButton(withTitle: Loc.t("Common.Cancel"))
            if a.runModal() != .alertFirstButtonReturn { return false }
        }
        closingNow = true
        canvas.resetInteraction()
        return true
    }

    func windowWillClose(_ notification: Notification) { HudTooltip.hideNow(); closed?() }
}

/// The editor's canvas: picture at a zoom with pan, markup, HUD (toolbar, flyouts, magnifier, notice, toast, zoom badge).
final class EditorCanvasView: FlippedView, AnnotationEditorHost, FlyoutHost {
    let session: EditSession
    let settings: AppSettings
    private(set) var editor: AnnotationEditor!
    let toolbar = ToolbarView()
    private let magnifier = MagnifierView()
    private let notice = NoticePlaque()
    private let toastLabel = HudLabel()
    private let zoomBadge = HudButton(size: CGSize(width: 60, height: 22), title: "")
    private var zoom: CGFloat = 1   // device pixels per image pixel
    private var fit = true
    private var offset = CGPoint.zero // image origin in view points
    private(set) var originalMode = false
    private var noticeDismissed = false
    private var panning: CGPoint?
    private var spaceDown = false
    private var toastTimer: Timer?
    var actionRequested: ((CaptureAction) -> Void)?
    var closeRequested: (() -> Void)?
    var restartRequested: (() -> Void)?
    var documentChanged: (() -> Void)?

    init(session: EditSession, settings: AppSettings) {
        self.session = session
        self.settings = settings
        super.init(frame: .zero)
        wantsLayer = true
        let beautifyOn = session.beautify != nil
        editor = AnnotationEditor(host: self, toolbar: toolbar, source: session.base, settings: settings,
                                  beautifyOn: beautifyOn, beautify: session.beautify ?? settings.beautify, persistBeautify: false,
                                  unitScale: NSScreen.main?.backingScaleFactor ?? 2)
        editor.document.load(session.annotations)
        editor.document.onChange { [weak self] in self?.documentChanged?() }
        editor.guidesChanged = { [weak self] in self?.needsDisplay = true }
        toolbar.configureForEditor(settings)
        toolbar.isHidden = false
        toolbar.actionRequested = { [weak self] a in self?.actionRequested?(a) }
        toolbar.cancelRequested = { [weak self] in self?.closeRequested?() }
        magnifier.isHidden = true
        toastLabel.isHidden = true
        toastLabel.background = RGBA(0x29, 0x25, 0x24, a: 0xF2)
        toastLabel.padding = CGSize(width: 12, height: 6)
        toastLabel.radius = 8
        toastLabel.font = .systemFont(ofSize: 12.5)
        zoomBadge.titleFont = .monospacedSystemFont(ofSize: 11, weight: .regular)
        zoomBadge.accent = false
        zoomBadge.wantsLayer = true
        zoomBadge.layer?.backgroundColor = Palette.overlayPanel.cgColor
        zoomBadge.layer?.cornerRadius = 7
        zoomBadge.action = { [weak self] in guard let self else { return }; self.fit ? self.setActual() : self.setFit() }
        notice.closeAction = { [weak self] in self?.noticeDismissed = true; self?.notice.isHidden = true }
        notice.restartAction = { [weak self] in self?.restartRequested?() }
        for v in [magnifier, notice, toastLabel, zoomBadge, toolbar] as [NSView] { addSubview(v) }
        editor.flyouts.attach(self)
        addTrackingArea(NSTrackingArea(rect: .zero, options: [.mouseMoved, .activeInKeyWindow, .inVisibleRect, .mouseEnteredAndExited], owner: self))
        updateNotice()
    }
    required init?(coder: NSCoder) { fatalError() }

    override var acceptsFirstResponder: Bool { true }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }

    private var backing: CGFloat { window?.backingScaleFactor ?? 2 }
    /// View points per image pixel.
    private var k: CGFloat { zoom / backing }

    override func layout() {
        super.layout()
        relayout()
    }

    func relayoutToolbar() {
        toolbar.layoutButtons()
        toolbar.setFrameOrigin(CGPoint(x: (bounds.width - toolbar.frame.width) / 2, y: bounds.height - toolbar.frame.height - 12))
        editor.flyouts.reposition()
    }

    private var viewport: CGRect { CGRect(x: 0, y: 0, width: bounds.width, height: bounds.height - 62) }

    private func relayout() {
        relayoutToolbar()
        let vp = viewport
        if fit {
            let f = min(1, min((vp.width - 32) / CGFloat(session.base.width), (vp.height - 32) / CGFloat(session.base.height)) * backing)
            zoom = max(0.05, f)
        }
        let w = CGFloat(session.base.width) * k, h = CGFloat(session.base.height) * k
        if fit || w <= vp.width { offset.x = (vp.width - w) / 2 } else { offset.x = clamp(offset.x, vp.width - w - 16, 16) }
        if fit || h <= vp.height { offset.y = (vp.height - h) / 2 } else { offset.y = clamp(offset.y, vp.height - h - 16, 16) }
        zoomBadge.title = Loc.f(fit ? "Editor.ZoomFit" : "Editor.Zoom", Int((zoom * 100).rounded()))
        zoomBadge.tip = Loc.t("Editor.ZoomTip")
        let bw = zoomBadge.intrinsicContentSize.width
        zoomBadge.frame = CGRect(x: bounds.width - bw - 10, y: bounds.height - 22 - 10, width: bw, height: 22)
        if !notice.isHidden { notice.layoutIn(width: bounds.width) }
        if !toastLabel.isHidden { toastLabel.setFrameOrigin(CGPoint(x: (bounds.width - toastLabel.frame.width) / 2, y: bounds.height - 64 - toastLabel.frame.height - 10)) }
        editor.relayoutTextField()
        needsDisplay = true
    }

    private func zoomAt(_ z: CGFloat, anchor: CGPoint) {
        let img = toImage(anchor)
        fit = false
        zoom = clamp(z, 0.1, 8)
        offset = CGPoint(x: anchor.x - img.x * k, y: anchor.y - img.y * k)
        relayout()
    }
    private func setFit() { fit = true; relayout() }
    private func setActual() { zoomAt(1, anchor: CGPoint(x: viewport.midX, y: viewport.midY)) }

    private func toImage(_ p: CGPoint) -> CGPoint { CGPoint(x: (p.x - offset.x) / k, y: (p.y - offset.y) / k) }
    private func pixel(_ p: CGPoint) -> PixelPoint {
        let i = toImage(p)
        return PixelPoint(x: clamp(Int(floor(i.x)), 0, session.base.width - 1), y: clamp(Int(floor(i.y)), 0, session.base.height - 1))
    }
    private func toolPoint(_ p: CGPoint) -> CGPoint {
        let i = toImage(p)
        return CGPoint(x: clamp(i.x, 0, CGFloat(session.base.width)), y: clamp(i.y, 0, CGFloat(session.base.height)))
    }
    private func local(_ e: NSEvent) -> CGPoint { convert(e.locationInWindow, from: nil) }

    // MARK: Drawing

    override func draw(_ dirtyRect: NSRect) {
        Theme.thumb.setFill(); bounds.fill()
        guard let ctx = NSGraphicsContext.current?.cgContext else { return }
        let w = CGFloat(session.base.width) * k, h = CGFloat(session.base.height) * k
        let r = CGRect(x: offset.x, y: offset.y, width: w, height: h)
        ctx.saveGState()
        ctx.setShadow(offset: CGSize(width: 0, height: -2), blur: 10, color: CGColor(gray: 0, alpha: 0.25))
        ctx.setFillColor(CGColor(gray: 1, alpha: 1)); ctx.fill(r)
        ctx.restoreGState()
        ctx.saveGState()
        ctx.clip(to: r)
        ctx.translateBy(x: offset.x, y: offset.y)
        ctx.scaleBy(x: k, y: k)
        ctx.interpolationQuality = zoom >= 2 ? .none : .high
        ctx.saveGState()
        ctx.translateBy(x: 0, y: CGFloat(session.base.height)); ctx.scaleBy(x: 1, y: -1)
        ctx.draw(session.base.cgImage, in: CGRect(x: 0, y: 0, width: session.base.width, height: session.base.height))
        ctx.restoreGState()
        if !originalMode { editor.document.render(ctx, RenderContext(source: session.base, forExport: false)) }
        let s = 1 / k
        if let m = editor.ruler.measurement { drawRuler(ctx, m, s) }
        for (wd, c) in [(3 * s, RGBA(255, 255, 255, a: 0xB3)), (s, Palette.accent)] {
            ctx.setStrokeColor(c.cgColor); ctx.setLineWidth(wd)
            for g in editor.snapper.guides {
                if g.orientation == .vertical { ctx.strokeLineSegments(between: [CGPoint(x: g.position, y: g.start), CGPoint(x: g.position, y: g.end)]) }
                else { ctx.strokeLineSegments(between: [CGPoint(x: g.start, y: g.position), CGPoint(x: g.end, y: g.position)]) }
            }
        }
        ctx.restoreGState()
    }

    private func drawRuler(_ ctx: CGContext, _ m: RulerMeasurement, _ s: CGFloat) {
        let a = m.start + CGVector(dx: 0.5, dy: 0.5), b = m.end + CGVector(dx: 0.5, dy: 0.5)
        ctx.setLineCap(.round)
        for (w, c) in [(4 * s, RGBA(255, 255, 255, a: 0xCC)), (2 * s, Palette.accent)] {
            ctx.setLineWidth(w); ctx.setStrokeColor(c.cgColor); ctx.strokeLineSegments(between: [a, b])
        }
        guard m.length >= 1 else { return }
        let text = NSAttributedString(string: m.label, attributes: [.font: NSFont.systemFont(ofSize: 12 * s, weight: .semibold), .foregroundColor: NSColor.white])
        let ts = text.size()
        let mid = CGPoint(x: (a.x + b.x) / 2, y: (a.y + b.y) / 2 - 18 * s)
        let box = CGRect(x: mid.x - ts.width / 2 - 8 * s, y: mid.y - ts.height / 2 - 4 * s, width: ts.width + 16 * s, height: ts.height + 8 * s)
        ctx.setFillColor(RGBA(0x29, 0x25, 0x24, a: 0xF2).cgColor)
        ctx.addPath(CGPath(roundedRect: box, cornerWidth: 6 * s, cornerHeight: 6 * s, transform: nil)); ctx.fillPath()
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(cgContext: ctx, flipped: true)
        text.draw(at: CGPoint(x: box.minX + 8 * s, y: box.minY + 4 * s))
        NSGraphicsContext.restoreGraphicsState()
    }

    // MARK: Host

    var optionsEnabled: Bool { true }
    var canUsePipette: Bool { !originalMode }
    var isClosing: Bool { window == nil }
    var hostHasGesture: Bool { editor.hasGesture || panning != nil }
    func pixelToHud(_ p: CGPoint) -> CGPoint { CGPoint(x: offset.x + p.x * k, y: offset.y + p.y * k) }
    func pixelsToHud(_ l: Double) -> Double { l * k }
    var hudView: NSView { self }
    var snapArea: CGRect { session.base.bounds.cgRect }
    func focusSurface() { window?.makeFirstResponder(self) }
    func resetInteraction() { editor.cancelGesture(); panning = nil }
    func refreshHover() { if let w = window { updateHover(convert(w.mouseLocationOutsideOfEventStream, from: nil)) } }
    func pipetteChanged(_ on: Bool) { if !on { magnifier.isHidden = true } }
    func beautifyChanged() {}
    func redraw() { needsDisplay = true }
    var flyoutLayer: NSView { self }
    var flyoutWorkArea: CGRect { bounds }
    var toolbarFlyoutsBelow: Bool { false }
    var toolbarFrame: CGRect { toolbar.frame }
    func shapeRect(_ a: Annotation) -> CGRect? {
        let b = a.bounds
        return b.isNull ? nil : CGRect(x: offset.x + b.minX * k, y: offset.y + b.minY * k, width: b.width * k, height: b.height * k)
    }

    func setOriginalMode(_ on: Bool) {
        guard originalMode != on else { return }
        resetInteraction()
        originalMode = on
        editor.drawingEnabled = !on
        if on { noticeDismissed = false }
        updateNotice()
        needsDisplay = true
    }

    func updateNotice() {
        var text: String?
        var restart = false
        if originalMode { text = Loc.t("Editor.NoticeOriginal"); restart = true }
        else if let m = session.message { text = m }
        else if !session.hasOriginal { text = Loc.t("Editor.NoticeNoOriginal") }
        notice.set(text: text ?? "", restart: restart)
        notice.isHidden = text == nil || noticeDismissed
        notice.layoutIn(width: bounds.width)
    }

    func toast(_ text: String) {
        toastLabel.text = text
        toastLabel.isHidden = false
        toastLabel.setFrameOrigin(CGPoint(x: (bounds.width - toastLabel.frame.width) / 2, y: bounds.height - 64 - toastLabel.frame.height - 10))
        toastTimer?.invalidate()
        toastTimer = Timer.scheduledTimer(withTimeInterval: 2.2, repeats: false) { [weak self] _ in self?.toastLabel.isHidden = true }
    }

    // MARK: Mouse

    override func mouseDown(with e: NSEvent) {
        window?.makeFirstResponder(self)
        let p = local(e)
        if spaceDown { panning = p; NSCursor.closedHand.set(); return }
        editor.commitTextEdit()
        if editor.pipetteOn { editor.pickColor(pixel(p)); return }
        let img = toImage(p)
        guard session.base.bounds.cgRect.contains(img) else { editor.deselect(); return }
        _ = editor.beginGesture(toolPoint(p))
    }

    override func mouseDragged(with e: NSEvent) {
        let p = local(e)
        if let start = panning {
            fit = false
            offset.x += p.x - start.x; offset.y += p.y - start.y
            panning = p
            relayout()
            return
        }
        if editor.hasGesture { editor.moveGesture(toolPoint(p)) }
    }

    override func mouseUp(with e: NSEvent) {
        if panning != nil { panning = nil; NSCursor.openHand.set(); return }
        editor.endGesture(toolPoint(local(e)))
    }

    override func otherMouseDown(with e: NSEvent) { panning = local(e) }
    override func otherMouseDragged(with e: NSEvent) { mouseDragged(with: e) }
    override func otherMouseUp(with e: NSEvent) { panning = nil }
    override func rightMouseDown(with e: NSEvent) { stepBack(close: false) }

    override func mouseMoved(with e: NSEvent) { updateHover(local(e)) }
    override func mouseExited(with event: NSEvent) { if !editor.pipetteOn { magnifier.isHidden = true } }

    private var wheelAccum: CGFloat = 0
    override func scrollWheel(with e: NSEvent) {
        if e.modifierFlags.contains(.command) {
            let f: CGFloat = e.scrollingDeltaY > 0 ? 1.1 : 1 / 1.1
            zoomAt(zoom * f, anchor: local(e)); return
        }
        if e.hasPreciseScrollingDeltas && e.phase != [] || e.momentumPhase != [] {
            // Trackpad two-finger scroll pans the picture (wheel notches change thickness, like Windows).
            fit = false
            offset.x += e.scrollingDeltaX; offset.y += e.scrollingDeltaY
            relayout(); return
        }
        wheelAccum += e.scrollingDeltaY
        while abs(wheelAccum) >= 1 { let d = wheelAccum > 0 ? 1 : -1; wheelAccum -= CGFloat(d); editor.adjustThickness(d) }
    }

    override func magnify(with e: NSEvent) { zoomAt(zoom * (1 + e.magnification), anchor: local(e)) }

    private func updateHover(_ p: CGPoint) {
        if let hit = hitTest(p), hit !== self { return }
        if spaceDown { NSCursor.openHand.set(); return }
        if editor.pipetteOn {
            let px = pixel(p)
            magnifier.isHidden = false
            magnifier.showColor = settings.isOn(.pipette)
            magnifier.update(session.base, px)
            var x = p.x + 20, y = p.y + 20
            if x + magnifier.frame.width > bounds.maxX { x = p.x - 20 - magnifier.frame.width }
            if y + magnifier.frame.height > bounds.maxY { y = p.y - 20 - magnifier.frame.height }
            magnifier.setFrameOrigin(CGPoint(x: x, y: y))
            NSCursor.crosshair.set(); return
        }
        let img = toImage(p)
        if !session.base.bounds.cgRect.contains(img) { NSCursor.arrow.set(); return }
        if toolbar.currentTool == .select { (editor.document.hitTest(img) != nil ? NSCursor.pointingHand : NSCursor.arrow).set() }
        else { editor.cursor(for: toolbar.currentTool).set() }
    }

    // MARK: Keys

    override func keyDown(with e: NSEvent) {
        if editor.isTextEditing { return }
        let cmd = e.modifierFlags.contains(.command)
        switch e.keyCode {
        case Key.space: spaceDown = true; NSCursor.openHand.set()
        case Key.esc: stepBack(close: true)
        case Key.c where cmd: actionRequested?(.copyExplicit)
        case Key.s where cmd: actionRequested?(.save)
        case Key.f3 where settings.actionAllowed(.pin): actionRequested?(.pin)
        case Key.zero where cmd: setFit()
        case Key.one where cmd: setActual()
        case Key.equal where cmd: zoomAt(zoom * 1.25, anchor: CGPoint(x: viewport.midX, y: viewport.midY))
        case Key.minus where cmd: zoomAt(zoom / 1.25, anchor: CGPoint(x: viewport.midX, y: viewport.midY))
        default:
            if !editor.handleKey(e, editing: true) { super.keyDown(with: e) }
        }
    }

    override func keyUp(with e: NSEvent) {
        if e.keyCode == Key.space { spaceDown = false; refreshHover() }
    }

    override func performKeyEquivalent(with e: NSEvent) -> Bool {
        guard window?.firstResponder === self, e.modifierFlags.contains(.command), e.keyCode != 13 /* ⌘W */ else { return super.performKeyEquivalent(with: e) }
        keyDown(with: e)
        return true
    }

    private func stepBack(close: Bool) {
        if hostHasGesture { resetInteraction(); return }
        if editor.pipetteOn { editor.setPipette(false); return }
        if editor.isTextEditing { editor.commitTextEdit(); return }
        if editor.ruler.measurement != nil { editor.ruler.clear(); return }
        if editor.document.selected != nil || toolbar.currentTool != .select { editor.deselect(); editor.setTool(.select); return }
        if close { closeRequested?() }
    }
}

/// Dark plaque at the top of the editor: how the shot was opened, with "Start over from the original" and ✕.
final class NoticePlaque: HudCard {
    private let label = NSTextField(wrappingLabelWithString: "")
    private let restart = stripButton(title: "", width: 60)
    private let close = stripButton(.symbol("xmark", 9))
    var closeAction: (() -> Void)?
    var restartAction: (() -> Void)?

    init() {
        super.init(frame: .zero)
        radius = 10
        label.font = .systemFont(ofSize: 12.5)
        label.textColor = Palette.overlayInk.nsColor
        restart.accent = true
        restart.action = { [weak self] in self?.restartAction?() }
        close.action = { [weak self] in self?.closeAction?() }
        for v in [label, restart, close] as [NSView] { addSubview(v) }
    }
    required init?(coder: NSCoder) { fatalError() }

    func set(text: String, restart on: Bool) {
        label.stringValue = text
        restart.title = Loc.t("Editor.Restart")
        restart.isHidden = !on
        close.tip = Loc.t("Editor.Hide")
    }

    func layoutIn(width: CGFloat) {
        let maxW = min(720, width - 32)
        let buttons: CGFloat = 26 + (restart.isHidden ? 0 : restart.intrinsicContentSize.width + 8)
        let textW = maxW - 24 - buttons - 8
        let th = ceil(label.sizeThatFits(CGSize(width: textW, height: 400)).height)
        let h = max(26, th) + 16
        let tw = min(textW, ceil(label.sizeThatFits(CGSize(width: 10_000, height: 400)).width))
        let w = 12 + tw + 8 + buttons + 12
        label.frame = CGRect(x: 12, y: (h - th) / 2, width: tw, height: th)
        close.frame = CGRect(x: w - 12 - 26, y: (h - 26) / 2, width: 26, height: 26)
        if !restart.isHidden {
            let rw = restart.intrinsicContentSize.width
            restart.frame = CGRect(x: close.frame.minX - 8 - rw, y: (h - 26) / 2, width: rw, height: 26)
        }
        frame = CGRect(x: (width - w) / 2, y: 12, width: w, height: h)
        needsDisplay = true
    }
}

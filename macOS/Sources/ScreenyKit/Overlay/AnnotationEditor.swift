import AppKit

/// What the overlay or the editor window gives the annotation editor (Editing/AnnotationEditor.cs IAnnotationEditorHost).
protocol AnnotationEditorHost: AnyObject {
    var optionsEnabled: Bool { get }
    var canUsePipette: Bool { get }
    var isClosing: Bool { get }
    var hostHasGesture: Bool { get }
    /// Screenshot pixel → point in `hudView` (where the text field lives).
    func pixelToHud(_ p: CGPoint) -> CGPoint
    func pixelsToHud(_ length: Double) -> Double
    var hudView: NSView { get }
    var snapArea: CGRect { get }
    func focusSurface()
    func resetInteraction()
    func refreshHover()
    func pipetteChanged(_ on: Bool)
    func beautifyChanged()
    func redraw()
}

/// Key codes are physical (ANSI positions), so shortcuts work with any keyboard layout (Russian included).
enum Key {
    static let a: UInt16 = 0, s: UInt16 = 1, c: UInt16 = 8, b: UInt16 = 11, e: UInt16 = 14, y: UInt16 = 16, t: UInt16 = 17
    static let z: UInt16 = 6, i: UInt16 = 34, p: UInt16 = 35, zero: UInt16 = 29, one: UInt16 = 18
    static let ret: UInt16 = 36, enter: UInt16 = 76, tab: UInt16 = 48, space: UInt16 = 49, backspace: UInt16 = 51, esc: UInt16 = 53
    static let forwardDelete: UInt16 = 117, f3: UInt16 = 99
    static let left: UInt16 = 123, right: UInt16 = 124, down: UInt16 = 125, up: UInt16 = 126
    static let minus: UInt16 = 27, equal: UInt16 = 24

    private static let letterCodes: [Character: UInt16] = ["a": 0, "s": 1, "c": 8, "b": 11, "e": 14, "y": 16, "t": 17, "z": 6, "i": 34, "p": 35]

    /// Key code for shortcut matching: on Latin layouts the printed letter decides (⌘Z on QWERTZ is the key labelled Z);
    /// on other layouts (Russian) the physical position does.
    static func code(_ e: NSEvent) -> UInt16 {
        if let ch = e.charactersIgnoringModifiers?.lowercased().first, ch.isASCII, ch.isLetter {
            return letterCodes[ch] ?? (letterCodes.values.contains(e.keyCode) ? 999 : e.keyCode)
        }
        return e.keyCode
    }

    static let toolKeys: [UInt16: ToolKind] = [
        18: .select, 19: .rect, 20: .ellipse, 21: .line, 23: .arrow, 22: .pen, 26: .highlighter, 28: .text, 25: .number, 29: .pixelate,
        27: .blur, 24: .ruler,
        83: .select, 84: .rect, 85: .ellipse, 86: .line, 87: .arrow, 88: .pen, 89: .highlighter, 91: .text, 92: .number, 82: .pixelate,
        78: .blur, 69: .ruler, // keypad
    ]
}

/// Text field placed on the screenshot while typing a text annotation.
final class AnnotationTextView: NSTextView {
    var commit: (() -> Void)?
    var cancel: (() -> Void)?
    override func keyDown(with event: NSEvent) {
        if hasMarkedText() { super.keyDown(with: event); return } // CJK input method is composing
        if event.keyCode == Key.esc { cancel?(); return }
        if (event.keyCode == Key.ret || event.keyCode == Key.enter) && !event.modifierFlags.contains(.shift) { commit?(); return }
        super.keyDown(with: event)
    }
    override func resignFirstResponder() -> Bool {
        let r = super.resignFirstResponder()
        if r { DispatchQueue.main.async { [weak self] in self?.commit?() } }
        return r
    }
}

/// Annotation editor without its own window: tools, text input, selection, options flyouts and palette, eyedropper,
/// ruler, undo/redo, background frame and drawing keys. Used by the overlay and by the editor window.
final class AnnotationEditor {
    static let snapThresholdPoints = 6.0

    unowned let host: AnnotationEditorHost
    let toolbar: ToolbarView
    let flyouts: FlyoutController
    let settings: AppSettings
    let document = AnnotationDocument()
    let source: PixelImage
    let options: StyleOptions
    let snapper: Snapper
    let ruler: RulerTool
    private var tools: [ToolKind: Tool] = [:]
    private let textTool: TextTool
    private var pipette = false
    private var activeTool: Tool?
    private var textEdit: (at: CGPoint, style: AnnotationStyle)?
    private var textView: AnnotationTextView?
    private var stripKey: String?
    private var gestureStarting = false
    private(set) var beautifyOn: Bool
    private(set) var beautify: BeautifyOptions
    private let persistBeautify: Bool
    var drawingEnabledValue = true
    /// Screenshot pixels per screen point: thickness and text size are chosen in points, like on a 100 % Windows display.
    let unitScale: CGFloat
    var guidesChanged: (() -> Void)?
    var rulerChanged: (() -> Void)?

    init(host: AnnotationEditorHost, toolbar: ToolbarView, source: PixelImage, settings: AppSettings,
         beautifyOn: Bool, beautify: BeautifyOptions, persistBeautify: Bool, unitScale: CGFloat = 1) {
        self.unitScale = max(1, unitScale)
        self.host = host; self.toolbar = toolbar; self.source = source; self.settings = settings
        self.flyouts = FlyoutController(toolbar: toolbar)
        let shift = { NSEvent.modifierFlags.contains(.shift) }
        snapper = Snapper(enabled: { settings.snapEnabled && !NSEvent.modifierFlags.contains(.option) },
                          threshold: { [unowned host] in AnnotationEditor.snapThresholdPoints / max(1e-6, host.pixelsToHud(1)) },
                          area: { [unowned host] in host.snapArea })
        textTool = TextTool(snap: snapper)
        ruler = RulerTool(constrain: shift)
        tools = [
            .select: SelectTool(snap: snapper),
            .rect: ShapeTool(.rect, constrain: shift, snap: snapper), .ellipse: ShapeTool(.ellipse, constrain: shift, snap: snapper),
            .line: ShapeTool(.line, constrain: shift, snap: snapper), .arrow: ShapeTool(.arrow, constrain: shift, snap: snapper),
            .pen: PenTool(highlighter: false), .highlighter: PenTool(highlighter: true), .text: textTool, .number: NumberTool(snap: snapper),
            .pixelate: ShapeTool(.pixelate, constrain: shift, snap: snapper), .blur: ShapeTool(.blur, constrain: shift, snap: snapper),
            .ruler: ruler,
        ]
        let color = RGBA.parse(settings.lastColor)?.opaque ?? Palette.colors[0]
        options = StyleOptions.from(settings, palette: color)
        self.beautifyOn = beautifyOn && settings.isOn(.beautify)
        self.beautify = beautify
        self.persistBeautify = persistBeautify

        snapper.guidesChanged = { [weak self] in self?.guidesChanged?() }
        ruler.changed = { [weak self] in self?.rulerChanged?(); self?.host.redraw() }
        textTool.editRequested = { [weak self] p, s in self?.beginTextEdit(p, s) }

        toolbar.applyFeatures(settings)
        toolbar.setActiveTool(.select)
        toolbar.setColor(color)
        toolbar.setThickness(settings.lastThickness)
        toolbar.setUndoState(false, false)
        toolbar.undoRequested = { [weak self] in self?.undo() }
        toolbar.redoRequested = { [weak self] in self?.redo() }
        toolbar.toolChanged = { [weak self] _ in self?.toolSwitched() }
        toolbar.colorPicked = { [weak self] c in self?.colorPicked(c) }
        toolbar.pipetteRequested = { [weak self] in self?.togglePipette() }
        toolbar.thicknessChanged = { [weak self] t in self?.settings.lastThickness = t }
        document.onChange { [weak self] in
            guard let self else { return }
            self.toolbar.setUndoState(self.document.undo.canUndo, self.document.undo.canRedo)
            self.refreshOptions()
            self.host.redraw()
        }
        let strip = flyouts.options
        strip.targetClicked = { [weak self] t in self?.targetClicked(t) }
        strip.strokeNoneClicked = { [weak self] in guard let self else { return }; self.editStyle(self.options.toggleStroke) }
        strip.fillNoneClicked = { [weak self] in guard let self else { return }; self.editStyle(self.options.toggleFill) }
        strip.fillOpacityClicked = { [weak self] in guard let self else { return }; self.editStyle(self.options.nextFillOpacity) }
        strip.backgroundNoneClicked = { [weak self] in guard let self else { return }; self.editStyle(self.options.toggleTextBackground) }
        strip.arrowStyleClicked = { [weak self] a in self?.editStyle { var s = $0; s.arrow = a; return s } }
        strip.textAlignClicked = { [weak self] a in self?.editStyle { var s = $0; s.textAlign = a; return s } }

        toolbar.setBeautify(self.beautifyOn)
        flyouts.beautify.show(beautify)
        toolbar.beautifyRequested = { [weak self] in self?.toggleBeautify() }
        flyouts.beautify.optionsChanged = { [weak self] o in self?.setBeautifyOptions(o) }
    }

    var currentTool: ToolKind { toolbar.currentTool }
    func style(for tool: ToolKind) -> AnnotationStyle { options.style(for: tool, color: toolbar.currentColor, thickness: Double(toolbar.currentThickness) * unitScale) }
    var currentStyle: AnnotationStyle { style(for: currentTool) }
    func cursor(for t: ToolKind) -> NSCursor { tools[t]!.cursor }
    var pipetteOn: Bool { pipette }
    var isTextEditing: Bool { textEdit != nil }
    var hasGesture: Bool { activeTool != nil }
    var toolGestureActive: Bool { activeTool?.isActive == true }

    var drawingEnabled: Bool {
        get { drawingEnabledValue }
        set {
            if drawingEnabledValue == newValue { return }
            if !newValue {
                commitTextEdit(); cancelGesture(); setPipette(false)
                if document.selected != nil { document.selected = nil; document.notify() }
                setTool(.select)
            }
            drawingEnabledValue = newValue
            toolbar.setDrawingEnabled(newValue)
            toolbar.setUndoState(document.undo.canUndo, document.undo.canRedo)
            stripKey = nil
            refreshOptions()
        }
    }

    // MARK: Gestures

    @discardableResult func beginGesture(_ p: CGPoint) -> Bool {
        guard drawingEnabledValue, let tool = tools[currentTool] else { return false }
        gestureStarting = true
        tool.down(p, document, currentStyle)
        gestureStarting = false
        if !tool.isActive { refreshOptions(); return false }
        activeTool = tool
        return true
    }

    func moveGesture(_ p: CGPoint) { activeTool?.move(p) }
    func refreshGesture(_ p: CGPoint) { if toolGestureActive { activeTool?.move(p) } }

    @discardableResult func endGesture(_ p: CGPoint) -> Bool {
        guard let t = activeTool else { return false }
        activeTool = nil
        t.up(p)
        refreshOptions()
        return true
    }

    func cancelGesture() {
        let t = activeTool
        activeTool = nil
        t?.cancel()
        if t != nil { refreshOptions() }
    }

    func undo() { if !toolGestureActive && drawingEnabledValue { document.undo.undo() } }
    func redo() { if !toolGestureActive && drawingEnabledValue { document.undo.redo() } }

    func adjustThickness(_ d: Int) {
        guard drawingEnabledValue else { return }
        toolbar.setThickness(toolbar.currentThickness + d)
        settings.lastThickness = toolbar.currentThickness
    }

    func deselect() {
        guard document.selected != nil else { return }
        document.selected = nil
        document.notify()
    }

    // MARK: Keys

    /// Drawing keys: ⌘Z/⇧⌘Z/⌘Y, Delete and arrows for the selected shape; in drawing mode also I, B and tool keys.
    func handleKey(_ e: NSEvent, editing: Bool) -> Bool {
        let cmd = e.modifierFlags.contains(.command), shift = e.modifierFlags.contains(.shift)
        let k = Key.code(e)
        switch k {
        case Key.i where editing && !cmd && settings.isOn(.pipette): togglePipette(); return true
        case Key.b where editing && !cmd && settings.isOn(.beautify): toggleBeautify(); return true
        case Key.z where cmd && shift: redo(); return true
        case Key.z where cmd: undo(); return true
        case Key.y where cmd: redo(); return true
        case Key.backspace, Key.forwardDelete:
            guard let s = document.selected else { return false }
            if !toolGestureActive && drawingEnabledValue { document.remove(s) }
            return true
        case Key.left, Key.right, Key.up, Key.down:
            guard let s = document.selected else { return false }
            if host.hostHasGesture || !drawingEnabledValue { return true }
            let step: CGFloat = shift || e.modifierFlags.contains(.option) ? 10 : 1
            let dx: CGFloat = k == Key.left ? -step : k == Key.right ? step : 0
            let dy: CGFloat = k == Key.up ? -step : k == Key.down ? step : 0
            document.move(s, CGVector(dx: dx, dy: dy))
            return true
        default:
            if editing && !cmd && drawingEnabledValue, let t = Key.toolKeys[e.keyCode], settings.toolAllowed(t) { setTool(t); return true }
            return false
        }
    }

    // MARK: Text

    private func beginTextEdit(_ p: CGPoint, _ style: AnnotationStyle) {
        commitTextEdit()
        textEdit = (p, style)
        let tv = AnnotationTextView(frame: .zero)
        tv.isRichText = false
        tv.drawsBackground = true
        tv.textContainerInset = .zero
        tv.textContainer?.lineFragmentPadding = 0
        tv.isHorizontallyResizable = false
        tv.isVerticallyResizable = false
        tv.textContainer?.widthTracksTextView = false
        tv.textContainer?.containerSize = CGSize(width: 100_000, height: 100_000)
        tv.insertionPointColor = .white
        tv.allowsUndo = true
        tv.wantsLayer = true
        tv.layer?.borderColor = NSColor.white.withAlphaComponent(0.67).cgColor
        tv.layer?.borderWidth = 1
        tv.commit = { [weak self] in self?.commitTextEdit() }
        tv.cancel = { [weak self] in self?.cancelTextEdit() }
        tv.delegate = textDelegate
        textDelegate.changed = { [weak self] in self?.sizeTextView() }
        textView = tv
        host.hudView.addSubview(tv)
        showTextEditorStyle(style)
        deselect()
        tv.window?.makeFirstResponder(tv)
        refreshOptions()
    }

    private let textDelegate = TextDelegate()
    final class TextDelegate: NSObject, NSTextViewDelegate {
        var changed: (() -> Void)?
        func textDidChange(_ notification: Notification) { changed?() }
    }

    private func showTextEditorStyle(_ style: AnnotationStyle) {
        guard let tv = textView, let e = textEdit else { return }
        let k = host.pixelsToHud(1)
        let font = NSFont.systemFont(ofSize: TextAnnotation.fontSize(style.thickness) * k, weight: .semibold)
        tv.font = font
        tv.textColor = style.color.nsColor
        tv.typingAttributes = [.font: font, .foregroundColor: style.color.nsColor]
        tv.textStorage?.setAttributes([.font: font, .foregroundColor: style.color.nsColor], range: NSRange(location: 0, length: tv.string.utf16.count))
        tv.backgroundColor = style.textBackground?.nsColor ?? NSColor.black.withAlphaComponent(0.2)
        tv.alignment = style.textAlign == .center ? .center : style.textAlign == .right ? .right : .left
        _ = e
        sizeTextView()
    }

    private func sizeTextView() {
        guard let tv = textView, let e = textEdit, let lm = tv.layoutManager, let tc = tv.textContainer else { return }
        lm.ensureLayout(for: tc)
        let used = lm.usedRect(for: tc)
        let font = tv.font ?? .systemFont(ofSize: 14)
        let h = max(used.height, ceil(font.ascender - font.descender + font.leading))
        let minW: CGFloat = e.style.textAlign == .left ? 40 : 8
        let w = max(minW, ceil(used.width) + 4)
        let origin = host.pixelToHud(e.at)
        // The box starts where the annotation will be drawn; centered/right text grows from the same anchor.
        tv.frame = CGRect(x: origin.x - 1, y: origin.y - 1, width: w, height: h + 2)
        tv.textContainer?.containerSize = CGSize(width: e.style.textAlign == .left ? 100_000 : w, height: 100_000)
    }

    func commitTextEdit() {
        guard let e = textEdit else { return }
        textEdit = nil
        let text = (textView?.string ?? "").replacingOccurrences(of: "\\s+$", with: "", options: .regularExpression)
        let tv = textView
        textView = nil
        tv?.commit = nil
        tv?.removeFromSuperview()
        if !text.isEmpty { document.add(TextAnnotation(e.at, text: text, style: e.style)) }
        host.focusSurface()
        refreshOptions()
    }

    func cancelTextEdit() {
        guard textEdit != nil else { return }
        textEdit = nil
        textView?.commit = nil
        textView?.removeFromSuperview()
        textView = nil
        host.focusSurface()
        refreshOptions()
    }

    /// Keeps the text field in place after the canvas zooms or scrolls.
    func relayoutTextField() { if textEdit != nil, let e = textEdit { showTextEditorStyle(e.style) } }

    // MARK: Options and palette

    private var optionsSubject: Annotation? { document.selected.flatMap { document.contains($0) ? $0 : nil } }

    private func currentSections() -> [OptionsSectionKind] {
        guard host.optionsEnabled else { return [] }
        let s = OptionsSections.sections(tool: currentTool, selected: optionsSubject, beautify: beautifyOn)
        return drawingEnabledValue ? s : s.filter { $0 == .beautify }
    }

    private func effectiveStyle() -> AnnotationStyle { optionsSubject?.style ?? textEdit?.style ?? currentStyle }

    func refreshOptions() {
        let sections = currentSections()
        let main = sections.first { $0 != .beautify }
        let style = effectiveStyle()
        let target = OptionsSections.effectiveTarget(sections, options.activeTarget)
        let opacity = style.fill?.opacityPercent ?? options.fillOpacity
        let key = "\(String(describing: main))|\(style)|\(target)|\(opacity)"
        if key != stripKey {
            stripKey = key
            flyouts.options.show(main, style: style, target: target, opacity: opacity)
            toolbar.markSwatch(StyleOptions.swatchColor(style, target))
        }
        let shape = currentTool == .select ? optionsSubject : nil
        flyouts.update(FlyoutState(enabled: host.optionsEnabled && !host.isClosing, tool: currentTool, section: main, shape: shape,
                                   shapeHidden: toolGestureActive || gestureStarting, beautifyOn: beautifyOn))
    }

    private func editStyle(_ op: (AnnotationStyle) -> AnnotationStyle) {
        if toolGestureActive || !drawingEnabledValue { return }
        let sections = currentSections()
        let subject = optionsSubject
        let before = effectiveStyle()
        let after = op(before)
        if after != before {
            if let subject { document.restyle(subject, after) }
            else if let e = textEdit { textEdit = (e.at, after); showTextEditorStyle(after) }
            for s in sections { options.absorb(after, s) }
            if after.color != before.color {
                toolbar.setColor(after.color)
                settings.lastColor = after.color.rgbHex
            }
            if after.thickness != before.thickness { toolbar.setThickness(Int((after.thickness / unitScale).rounded())) }
            options.save(to: settings)
        }
        stripKey = nil
        refreshOptions()
    }

    private func colorPicked(_ c: RGBA) {
        let target = OptionsSections.effectiveTarget(currentSections(), options.activeTarget)
        editStyle { [options] in options.paint($0, target, c) }
        if optionsSubject == nil && textEdit == nil && target == .color {
            toolbar.setColor(c.opaque)
            settings.lastColor = c.opaque.rgbHex
        }
    }

    private func targetClicked(_ t: PaintTarget) {
        options.activeTarget = t
        let style = effectiveStyle()
        let shape = currentSections().contains(.shape)
        if t == .fill && style.fill == nil { editStyle(options.toggleFill) }
        else if t == .background && style.textBackground == nil { editStyle(options.toggleTextBackground) }
        else if t == .color && shape && !style.strokeEnabled { editStyle(options.toggleStroke) }
        else { stripKey = nil; refreshOptions() }
    }

    func invalidateOptions() { stripKey = nil }

    func setTool(_ k: ToolKind) {
        guard settings.toolAllowed(k) else { return }
        toolbar.setActiveTool(k)
        toolSwitched()
    }

    private func toolSwitched() {
        commitTextEdit()
        setPipette(false)
        if currentTool != .ruler { ruler.clear() }
        if currentTool != .select { deselect() }
        refreshOptions()
        flyouts.onToolPicked(currentTool)
        host.refreshHover()
    }

    func applyFeatures() {
        toolbar.applyFeatures(settings)
        if pipette && !settings.isOn(.pipette) { setPipette(false) }
        if !settings.toolAllowed(currentTool) { setTool(.select) }
        if beautifyOn && !settings.isOn(.beautify) { applyBeautify(false, persist: false) }
    }

    // MARK: Eyedropper

    func togglePipette() { setPipette(!pipette) }

    func setPipette(_ on0: Bool) {
        var on = on0
        if on && (!host.canUsePipette || !drawingEnabledValue || !settings.isOn(.pipette)) { on = false }
        if pipette == on { toolbar.setPipette(on); return }
        if on { commitTextEdit(); host.resetInteraction() }
        pipette = on
        toolbar.setPipette(on)
        host.pipetteChanged(on)
        if !host.isClosing { host.refreshHover() }
    }

    func pickColor(_ p: PixelPoint) {
        let c = source.color(at: p)
        setPipette(false)
        colorPicked(c.opaque)
    }

    // MARK: Background frame

    func toggleBeautify() { setBeautify(!beautifyOn) }

    func setBeautify(_ on: Bool) {
        if on && !settings.isOn(.beautify) { return }
        applyBeautify(on, persist: persistBeautify)
    }

    private func applyBeautify(_ on: Bool, persist: Bool) {
        if host.isClosing { return }
        beautifyOn = on
        if persist { settings.beautifyEnabled = on }
        toolbar.setBeautify(on)
        refreshOptions()
        flyouts.onBeautifyToggled(on)
        host.beautifyChanged()
    }

    func setBeautifyOptions(_ o: BeautifyOptions) {
        beautify = o
        if persistBeautify { settings.beautify = o }
        flyouts.beautify.show(o)
        if flyouts.beautifyFlyout.isOpen { flyouts.openBeautify() }
        host.beautifyChanged()
    }
}

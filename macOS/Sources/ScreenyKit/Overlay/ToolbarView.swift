import AppKit

/// Inline toolbar: tools, palette, eyedropper, thickness, undo/redo, finishing actions (ToolbarControl.xaml).
final class ToolbarView: HudCard {
    static let minThickness = 1, maxThickness = 20

    struct ToolDef { let kind: ToolKind; let icon: Icon; let nameKey: String; let keyText: String }

    static let tools: [ToolDef] = [
        ToolDef(kind: .select, icon: .symbol("cursorarrow", 14), nameKey: "Tool.Select", keyText: "1"),
        ToolDef(kind: .rect, icon: .symbol("rectangle", 15), nameKey: "Tool.Rect", keyText: "2"),
        ToolDef(kind: .ellipse, icon: .symbol("circle", 15), nameKey: "Tool.Ellipse", keyText: "3"),
        ToolDef(kind: .line, icon: .symbol("line.diagonal", 15), nameKey: "Tool.Line", keyText: "4"),
        ToolDef(kind: .arrow, icon: .symbol("arrow.up.right", 14), nameKey: "Tool.Arrow", keyText: "5"),
        ToolDef(kind: .pen, icon: .symbol("pencil", 15), nameKey: "Tool.Pen", keyText: "6"),
        ToolDef(kind: .highlighter, icon: .symbol("highlighter", 15), nameKey: "Tool.Highlighter", keyText: "7"),
        ToolDef(kind: .text, icon: .symbol("textformat", 14), nameKey: "Tool.Text", keyText: "8"),
        ToolDef(kind: .number, icon: .symbol("1.circle", 15), nameKey: "Tool.Number", keyText: "9"),
        ToolDef(kind: .pixelate, icon: .symbol("checkerboard.rectangle", 15), nameKey: "Tool.Pixelate", keyText: "0"),
        ToolDef(kind: .blur, icon: .symbol("circle.dotted", 15), nameKey: "Tool.Blur", keyText: "-"),
        ToolDef(kind: .ruler, icon: .symbol("ruler", 14), nameKey: "Tool.Ruler", keyText: "="),
    ]

    var actionRequested: ((CaptureAction) -> Void)?
    var textModeRequested: (() -> Void)?
    var cancelRequested: (() -> Void)?
    var undoRequested: (() -> Void)?
    var redoRequested: (() -> Void)?
    var toolChanged: ((ToolKind) -> Void)?
    var colorPicked: ((RGBA) -> Void)?
    var pipetteRequested: (() -> Void)?
    var beautifyRequested: (() -> Void)?
    var thicknessChanged: ((Int) -> Void)?

    private(set) var toolButtons: [ToolKind: HudButton] = [:]
    private var swatches: [(RGBA, SwatchView)] = []
    let pipetteButton = HudButton(size: CGSize(width: 32, height: 30), icon: .symbol("eyedropper", 14))
    private let thicknessBox = ThicknessView()
    private let undoButton = HudButton(size: CGSize(width: 32, height: 30), icon: .symbol("arrow.uturn.backward", 14))
    private let redoButton = HudButton(size: CGSize(width: 32, height: 30), icon: .symbol("arrow.uturn.forward", 14))
    let beautifyButton = HudButton(size: CGSize(width: 32, height: 30), icon: .sparkle)
    private let ocrButton = HudButton(size: CGSize(width: 30, height: 30), title: "OCR")
    private let pinButton = HudButton(size: CGSize(width: 32, height: 30), icon: .symbol("pin", 14))
    private let saveButton = HudButton(size: CGSize(width: 32, height: 30), icon: .symbol("square.and.arrow.down", 14))
    private let cancelButton = HudButton(size: CGSize(width: 32, height: 30), icon: .symbol("xmark", 14))
    private let copyButton = HudButton(size: CGSize(width: 60, height: 30), title: "")
    private var separators: [HudSeparator] = []

    private(set) var currentTool = ToolKind.select
    private(set) var currentColor = Palette.colors[0]
    private(set) var currentThickness = 3
    private var canUndo = false, canRedo = false, textMode = false, drawingDisabled = false, forEditor = false
    private var settings: AppSettings?
    private var observer: NSObjectProtocol?

    override init(frame: NSRect) {
        super.init(frame: frame)
        for t in Self.tools {
            let b = HudButton(size: CGSize(width: 32, height: 30), icon: t.icon)
            b.showsDot = OptionsSections.forTool(t.kind) != nil
            let kind = t.kind
            b.action = { [weak self] in self?.setActiveTool(kind); self?.toolChanged?(kind) }
            toolButtons[t.kind] = b
            addSubview(b)
        }
        for c in Palette.colors {
            let s = SwatchView(color: c)
            s.action = { [weak self] in self?.colorPicked?(c) }
            swatches.append((c, s))
            addSubview(s)
        }
        pipetteButton.action = { [weak self] in self?.pipetteRequested?() }
        thicknessBox.wheel = { [weak self] d in
            guard let self else { return }
            self.setThickness(self.currentThickness + d)
            self.thicknessChanged?(self.currentThickness)
        }
        undoButton.action = { [weak self] in self?.undoRequested?() }
        redoButton.action = { [weak self] in self?.redoRequested?() }
        beautifyButton.action = { [weak self] in self?.beautifyRequested?() }
        ocrButton.titleFont = NSFont.systemFont(ofSize: 12, weight: .semibold)
        ocrButton.action = { [weak self] in self?.textModeRequested?() }
        pinButton.action = { [weak self] in self?.actionRequested?(.pin) }
        saveButton.action = { [weak self] in self?.actionRequested?(.save) }
        cancelButton.action = { [weak self] in self?.cancelRequested?() }
        copyButton.accent = true
        copyButton.leadingIcon = .symbol("doc.on.doc", 12)
        copyButton.action = { [weak self] in self?.actionRequested?(.copyExplicit) }
        for v in [pipetteButton, thicknessBox, undoButton, redoButton, beautifyButton, ocrButton, pinButton, saveButton, cancelButton, copyButton] as [NSView] { addSubview(v) }
        for _ in 0..<3 { let s = HudSeparator(); separators.append(s); addSubview(s) }
        setThickness(3)
        setColor(currentColor)
        setActiveTool(.select)
        applyTexts()
        observer = NotificationCenter.default.addObserver(forName: Loc.changed, object: nil, queue: .main) { [weak self] _ in self?.applyTexts(); self?.layoutButtons() }
    }
    required init?(coder: NSCoder) { fatalError() }
    deinit { if let o = observer { NotificationCenter.default.removeObserver(o) } }

    private func applyTexts() {
        for t in Self.tools { toolButtons[t.kind]?.tip = Loc.f("Tool.WithKey", Loc.t(t.nameKey), t.keyText) }
        pipetteButton.tip = Loc.t("Toolbar.Pipette")
        thicknessBox.toolTipText = Loc.t("Toolbar.Thickness")
        undoButton.tip = Loc.t("Toolbar.Undo")
        redoButton.tip = Loc.t("Toolbar.Redo").replacingOccurrences(of: "⌘Y", with: "⇧⌘Z")
        beautifyButton.tip = Loc.t("Toolbar.Beautify")
        pinButton.tip = Loc.t("Toolbar.Pin")
        saveButton.tip = Loc.t("Toolbar.SaveAs")
        cancelButton.tip = Loc.t(forEditor ? "Toolbar.Close" : "Toolbar.Cancel")
        copyButton.title = Loc.t(textMode ? "Toolbar.CopyText" : "Common.Copy")
        ocrButton.tip = Loc.t(textMode ? "Toolbar.OcrBack" : "Toolbar.Ocr")
        copyButton.tip = Loc.t(textMode ? "Toolbar.CopyTextTip"
            : !forEditor ? "Toolbar.CopyEnter"
            : settings?.isOn(.history) == true ? "Toolbar.CopyEditorHistory" : "Toolbar.CopyEditor")
    }

    /// Lays out visible controls in one row and sizes the card.
    func layoutButtons() {
        var x: CGFloat = 5
        let h: CGFloat = 30, y: CGFloat = 5
        func place(_ v: NSView, width: CGFloat? = nil, margin: CGFloat = 1) {
            guard !v.isHidden else { return }
            x += margin
            let w = width ?? (v as? HudButton)?.intrinsicContentSize.width ?? v.frame.width
            v.frame = CGRect(x: x, y: y + (h - v.frame.height) / 2, width: w, height: v.frame.height)
            if v is HudButton { v.frame.size.height = h; v.frame.origin.y = y }
            x += w + margin
        }
        for t in Self.tools { if let b = toolButtons[t.kind] { place(b) } }
        place(separators[0])
        for (_, s) in swatches { place(s, margin: 2) }
        x += 1
        place(pipetteButton)
        place(thicknessBox, margin: 3)
        place(separators[1])
        place(undoButton); place(redoButton)
        place(separators[2])
        place(beautifyButton); place(ocrButton); place(pinButton); place(saveButton); place(cancelButton)
        x += 2
        place(copyButton)
        setFrameSize(CGSize(width: x + 5, height: h + 10))
        needsDisplay = true
    }

    func button(for kind: ToolKind) -> HudButton? { toolButtons[kind] }

    func setActiveTool(_ kind: ToolKind) {
        currentTool = kind
        for (k, b) in toolButtons { b.isChecked = k == kind }
    }

    func setColor(_ c: RGBA) {
        currentColor = c
        markSwatch(c)
        thicknessBox.color = c
    }

    func markSwatch(_ c: RGBA?) { for (col, s) in swatches { s.marked = col == c } }
    func setPipette(_ on: Bool) { pipetteButton.isChecked = on }
    func setBeautify(_ on: Bool) { beautifyButton.isChecked = on }

    func setThickness(_ t: Int) {
        currentThickness = clamp(t, Self.minThickness, Self.maxThickness)
        thicknessBox.thickness = currentThickness
    }

    func setUndoState(_ u: Bool, _ r: Bool) {
        canUndo = u; canRedo = r
        undoButton.isEnabled = u && !textMode && !drawingDisabled
        redoButton.isEnabled = r && !textMode && !drawingDisabled
    }

    func setDrawingEnabled(_ on: Bool) { drawingDisabled = !on; applyDrawingAvailability() }

    private func applyDrawingAvailability() {
        let off = textMode || drawingDisabled
        for b in toolButtons.values { b.isEnabled = !off }
        for (_, s) in swatches { s.isEnabled = !off }
        thicknessBox.alphaValue = off ? 0.35 : 1
        thicknessBox.isEnabled = !off
        pipetteButton.isEnabled = !off
        setUndoState(canUndo, canRedo)
    }

    func configureForEditor(_ s: AppSettings) { forEditor = true; applyFeatures(s) }

    /// Buttons of features turned off disappear completely; OCR is never shown in the editor window.
    func applyFeatures(_ s: AppSettings) {
        settings = s
        ocrButton.isHidden = !s.isOn(.ocr) || forEditor
        pinButton.isHidden = !s.actionAllowed(.pin)
        beautifyButton.isHidden = !s.isOn(.beautify)
        pipetteButton.isHidden = !s.isOn(.pipette)
        for (k, b) in toolButtons { b.isHidden = !s.toolAllowed(k) }
        applyTexts()
        layoutButtons()
    }

    func setTextMode(_ on: Bool) {
        textMode = on
        ocrButton.isChecked = on
        applyDrawingAvailability()
        applyTexts()
        layoutButtons()
    }
}

/// Palette color: an 18-pt circle with a ring; the marked one has a white ring.
final class SwatchView: FlippedView {
    let color: RGBA
    var marked = false { didSet { if marked != oldValue { needsDisplay = true } } }
    var isEnabled = true { didSet { alphaValue = isEnabled ? 1 : 0.35 } }
    var action: (() -> Void)?

    init(color: RGBA) {
        self.color = color
        super.init(frame: CGRect(x: 0, y: 0, width: 18, height: 18))
        toolTip = nil
    }
    required init?(coder: NSCoder) { fatalError() }

    override func draw(_ dirtyRect: NSRect) {
        let r = bounds.insetBy(dx: 1, dy: 1)
        color.nsColor.setFill()
        NSBezierPath(ovalIn: r).fill()
        let ring = NSBezierPath(ovalIn: r)
        ring.lineWidth = 2
        (marked ? NSColor.white : NSColor.white.withAlphaComponent(0x50 / 255.0)).setStroke()
        ring.stroke()
    }
    override func mouseDown(with event: NSEvent) { if isEnabled { action?() } }
    override func resetCursorRects() { addCursorRect(bounds, cursor: .pointingHand) }
    override func acceptsFirstMouse(for event: NSEvent?) -> Bool { true }
    override func mouseEntered(with event: NSEvent) { HudTooltip.show(color.rgbHex, for: self) }
    override func mouseExited(with event: NSEvent) { HudTooltip.hide(for: self) }
    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self))
    }
}

/// Thickness: a dot in the current color sized by thickness, with the number; the wheel changes it.
final class ThicknessView: FlippedView {
    var thickness = 3 { didSet { needsDisplay = true } }
    var color = Palette.colors[0] { didSet { needsDisplay = true } }
    var wheel: ((Int) -> Void)?
    var toolTipText = ""
    var isEnabled = true

    init() { super.init(frame: CGRect(x: 0, y: 0, width: 30, height: 30)) }
    required init?(coder: NSCoder) { fatalError() }

    override func draw(_ dirtyRect: NSRect) {
        let d = CGFloat(3 + thickness)
        color.nsColor.setFill()
        NSBezierPath(ovalIn: CGRect(x: bounds.midX - d / 2, y: bounds.midY - d / 2, width: d, height: d)).fill()
        let s = NSAttributedString(string: "\(thickness)", attributes: [
            .font: NSFont.monospacedSystemFont(ofSize: 9, weight: .regular), .foregroundColor: Palette.overlayInk70.nsColor,
        ])
        let sz = s.size()
        s.draw(at: CGPoint(x: bounds.maxX - sz.width, y: bounds.maxY - sz.height))
    }

    private var accum: CGFloat = 0
    override func scrollWheel(with event: NSEvent) {
        guard isEnabled else { return }
        accum += event.hasPreciseScrollingDeltas ? event.scrollingDeltaY / 8 : event.scrollingDeltaY
        while abs(accum) >= 1 { let d = accum > 0 ? 1 : -1; accum -= CGFloat(d); wheel?(d) }
    }
    override func mouseEntered(with event: NSEvent) { HudTooltip.show(toolTipText, for: self) }
    override func mouseExited(with event: NSEvent) { HudTooltip.hide(for: self) }
    override func mouseDown(with event: NSEvent) {}
    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self))
    }
}

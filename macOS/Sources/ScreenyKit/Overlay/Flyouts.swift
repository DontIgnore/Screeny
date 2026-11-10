import AppKit

/// Vertical stack of horizontal rows inside a flyout (FlyoutStyles.xaml Row/Label).
class FlyoutSection: FlippedView {
    struct Row { let label: HudText?; let items: [NSView] }
    var rows: [Row] = []
    var horizontal = false

    func addRow(_ labelKey: String?, _ items: [NSView]) {
        let label = labelKey.map { HudText(key: $0) }
        if let label { addSubview(label) }
        items.forEach(addSubview)
        rows.append(Row(label: label, items: items))
    }

    /// Lays rows out; returns the content size.
    @discardableResult func layoutRows() -> CGSize {
        var y: CGFloat = 0, maxW: CGFloat = 0
        let labelW = max(82, rows.compactMap { $0.label?.fitWidth }.max() ?? 0)
        for r in rows {
            var x: CGFloat = 0
            let h: CGFloat = r.items.map { $0.frame.height }.max() ?? 26
            if let l = r.label { l.frame = CGRect(x: 4, y: y + 2, width: labelW, height: h); x = 4 + labelW + 6 }
            for v in r.items where !v.isHidden {
                x += 1
                let w = (v as? HudButton)?.intrinsicContentSize.width ?? v.frame.width
                v.frame = CGRect(x: x, y: y + 2 + (h - v.frame.height) / 2, width: w, height: v.frame.height)
                x += w + 1
            }
            maxW = max(maxW, x)
            y += h + 4
        }
        let size = CGSize(width: maxW, height: y)
        setFrameSize(size)
        return size
    }
}

/// Gray label of a flyout row, re-localized on language change.
final class HudText: FlippedView {
    let key: String
    init(key: String) { self.key = key; super.init(frame: .zero) }
    required init?(coder: NSCoder) { fatalError() }
    var text: NSAttributedString {
        NSAttributedString(string: Loc.t(key), attributes: [.font: NSFont.systemFont(ofSize: 12), .foregroundColor: Palette.overlayInk70.nsColor])
    }
    var fitWidth: CGFloat { ceil(text.size().width) }
    override func draw(_ dirtyRect: NSRect) {
        let s = text, sz = s.size()
        s.draw(at: CGPoint(x: 0, y: (bounds.height - sz.height) / 2))
    }
    override func hitTest(_ point: NSPoint) -> NSView? { nil }
}

/// 26×26 color sample with an accent ring when active; red slash when the part is off.
final class SwatchButton: HudButton {
    enum Kind { case stroke, fill, text }
    let kind: Kind
    var color: RGBA? { didSet { needsDisplay = true } }
    var active = false { didSet { needsDisplay = true } }

    init(kind: Kind) {
        self.kind = kind
        super.init(size: CGSize(width: 26, height: 26))
    }
    required init?(coder: NSCoder) { fatalError() }

    override func draw(_ dirtyRect: NSRect) {
        if hovered { Palette.overlayHover.nsColor.setFill(); NSBezierPath(roundedRect: bounds, xRadius: 7, yRadius: 7).fill() }
        if active {
            let ring = NSBezierPath(roundedRect: bounds.insetBy(dx: 1, dy: 1), xRadius: 7, yRadius: 7)
            ring.lineWidth = 2; Palette.accent.nsColor.setStroke(); ring.stroke()
        }
        let chip = bounds.insetBy(dx: 4, dy: 4)
        if let c = color {
            if kind == .stroke {
                let p = NSBezierPath(roundedRect: chip.insetBy(dx: 1.5, dy: 1.5), xRadius: 3, yRadius: 3)
                p.lineWidth = 3; c.nsColor.setStroke(); p.stroke()
            } else {
                c.nsColor.setFill(); NSBezierPath(roundedRect: chip, xRadius: 4, yRadius: 4).fill()
                if kind == .fill {
                    let p = NSBezierPath(roundedRect: chip.insetBy(dx: 0.5, dy: 0.5), xRadius: 4, yRadius: 4)
                    Palette.overlayDivider.nsColor.setStroke(); p.stroke()
                }
            }
        } else {
            let p = NSBezierPath(roundedRect: chip.insetBy(dx: 0.5, dy: 0.5), xRadius: 4, yRadius: 4)
            Palette.overlayDivider.nsColor.setStroke(); p.stroke()
            let slash = NSBezierPath()
            slash.move(to: CGPoint(x: chip.minX + 2, y: chip.maxY - 2)); slash.line(to: CGPoint(x: chip.maxX - 2, y: chip.minY + 2))
            slash.lineWidth = 2; RGBA(hex: 0xFF6B61).nsColor.setStroke(); slash.stroke()
        }
    }
}

func stripButton(_ icon: Icon? = nil, title: String? = nil, width: CGFloat = 26) -> HudButton {
    let b = HudButton(size: CGSize(width: width, height: 26), icon: icon, title: title)
    b.cornerRadius = 6
    return b
}

/// Options of the active tool or the selected shape (OptionsPanelControl.xaml).
final class OptionsPanelView: FlippedView {
    var targetClicked: ((PaintTarget) -> Void)?
    var strokeNoneClicked: (() -> Void)?
    var fillNoneClicked: (() -> Void)?
    var fillOpacityClicked: (() -> Void)?
    var backgroundNoneClicked: (() -> Void)?
    var arrowStyleClicked: ((ArrowStyle) -> Void)?
    var textAlignClicked: ((TextAlign) -> Void)?

    private let shape = FlyoutSection(), arrow = FlyoutSection(), text = FlyoutSection()
    private let strokeSwatch = SwatchButton(kind: .stroke), fillSwatch = SwatchButton(kind: .fill)
    private let strokeNone = stripButton(.text("⊘", .systemFont(ofSize: 14))), fillNone = stripButton(.text("⊘", .systemFont(ofSize: 14)))
    private let fillOpacity = stripButton(title: "40 %", width: 44)
    private var arrowButtons: [ArrowStyle: HudButton] = [:]
    private let textSwatch = SwatchButton(kind: .text), bgSwatch = SwatchButton(kind: .fill)
    private let bgNone = stripButton(.text("⊘", .systemFont(ofSize: 14)))
    private var alignButtons: [TextAlign: HudButton] = [:]
    private(set) var current: OptionsSectionKind?

    init() {
        super.init(frame: .zero)
        fillOpacity.titleFont = .monospacedSystemFont(ofSize: 12, weight: .regular)
        shape.addRow("Options.Stroke", [strokeSwatch, strokeNone])
        shape.addRow("Options.Fill", [fillSwatch, fillNone, fillOpacity])
        strokeSwatch.action = { [weak self] in self?.targetClicked?(.color) }
        fillSwatch.action = { [weak self] in self?.targetClicked?(.fill) }
        strokeNone.action = { [weak self] in self?.strokeNoneClicked?() }
        fillNone.action = { [weak self] in self?.fillNoneClicked?() }
        fillOpacity.action = { [weak self] in self?.fillOpacityClicked?() }

        var arrowItems: [NSView] = []
        for style in ArrowStyle.allCases {
            let b = HudButton(size: CGSize(width: 36, height: 30), icon: .custom { r, c in OptionsPanelView.drawArrowIcon(style, r, c) })
            b.cornerRadius = 6
            b.action = { [weak self] in self?.arrowStyleClicked?(style) }
            arrowButtons[style] = b
            arrowItems.append(b)
        }
        arrow.addRow(nil, arrowItems)

        text.addRow("Options.Text", [textSwatch])
        text.addRow("Options.Background", [bgSwatch, bgNone])
        textSwatch.action = { [weak self] in self?.targetClicked?(.color) }
        bgSwatch.action = { [weak self] in self?.targetClicked?(.background) }
        bgNone.action = { [weak self] in self?.backgroundNoneClicked?() }
        var alignItems: [NSView] = []
        for (a, sym) in [(TextAlign.left, "text.alignleft"), (.center, "text.aligncenter"), (.right, "text.alignright")] {
            let b = stripButton(.symbol(sym, 13))
            b.action = { [weak self] in self?.textAlignClicked?(a) }
            alignButtons[a] = b
            alignItems.append(b)
        }
        text.addRow("Options.Align", alignItems)
        for s in [shape, arrow, text] { s.isHidden = true; addSubview(s) }
        applyTips()
    }
    required init?(coder: NSCoder) { fatalError() }

    private func applyTips() {
        strokeSwatch.tip = Loc.t("Options.StrokeSwatch"); fillSwatch.tip = Loc.t("Options.FillSwatch")
        fillOpacity.tip = Loc.t("Options.FillOpacity")
        textSwatch.tip = Loc.t("Options.TextSwatch"); bgSwatch.tip = Loc.t("Options.BackgroundSwatch")
        arrowButtons[.normal]?.tip = Loc.t("Options.ArrowNormal"); arrowButtons[.double]?.tip = Loc.t("Options.ArrowDouble")
        arrowButtons[.dashed]?.tip = Loc.t("Options.ArrowDashed"); arrowButtons[.thick]?.tip = Loc.t("Options.ArrowThick")
        alignButtons[.left]?.tip = Loc.t("Options.AlignLeft"); alignButtons[.center]?.tip = Loc.t("Options.AlignCenter")
        alignButtons[.right]?.tip = Loc.t("Options.AlignRight")
    }

    static func drawArrowIcon(_ style: ArrowStyle, _ r: CGRect, _ c: NSColor) {
        // 22×12 icon box centered (OptionsPanelControl.xaml paths).
        let ox = r.midX - 11, oy = r.midY - 6
        func p(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: ox + x, y: oy + y) }
        c.setFill(); c.setStroke()
        let line = NSBezierPath(), head = NSBezierPath()
        line.lineWidth = 2; line.lineCapStyle = .butt
        switch style {
        case .normal:
            line.move(to: p(1, 6)); line.line(to: p(14, 6))
            head.move(to: p(13, 1.5)); head.line(to: p(21, 6)); head.line(to: p(13, 10.5)); head.close()
        case .double:
            line.move(to: p(8, 6)); line.line(to: p(14, 6))
            head.move(to: p(13, 1.5)); head.line(to: p(21, 6)); head.line(to: p(13, 10.5)); head.close()
            head.move(to: p(9, 1.5)); head.line(to: p(1, 6)); head.line(to: p(9, 10.5)); head.close()
        case .dashed:
            line.move(to: p(1, 6)); line.line(to: p(13, 6))
            line.setLineDash([3, 2], count: 2, phase: 0)
            head.move(to: p(13, 1.5)); head.line(to: p(21, 6)); head.line(to: p(13, 10.5)); head.close()
        case .thick:
            head.move(to: p(1, 5.6)); head.line(to: p(12, 4)); head.line(to: p(12, 0.5)); head.line(to: p(21, 6))
            head.line(to: p(12, 11.5)); head.line(to: p(12, 8)); head.line(to: p(1, 6.4)); head.close()
        }
        line.stroke()
        head.lineJoinStyle = .round
        head.fill(); head.lineWidth = 1; head.stroke()
    }

    /// Shows one section; returns its size (zero for none).
    @discardableResult
    func show(_ section: OptionsSectionKind?, style: AnnotationStyle, target: PaintTarget, opacity: Int) -> CGSize {
        current = section
        applyTips()
        shape.isHidden = section != .shape; arrow.isHidden = section != .arrow; text.isHidden = section != .text
        switch section {
        case .shape:
            strokeSwatch.color = style.strokeEnabled ? style.color : nil
            strokeSwatch.active = target == .color
            fillSwatch.color = style.fill
            fillSwatch.active = target == .fill
            strokeNone.isChecked = !style.strokeEnabled
            strokeNone.tip = Loc.t(style.strokeEnabled ? "Options.NoStroke" : "Options.RestoreStroke")
            strokeNone.isEnabled = !style.strokeEnabled || style.canDisableStroke
            fillNone.isChecked = style.fill == nil
            fillNone.tip = Loc.t(style.fill == nil ? "Options.RestoreFill" : "Options.NoFill")
            fillNone.isEnabled = style.fill == nil || style.canDisableFill
            if !strokeNone.isEnabled { strokeNone.tip = Loc.t("Options.NeedStrokeOrFill") }
            if !fillNone.isEnabled { fillNone.tip = Loc.t("Options.NeedStrokeOrFill") }
            fillOpacity.title = "\(opacity) %"
            let s = shape.layoutRows(); setFrameSize(s); return s
        case .arrow:
            for (a, b) in arrowButtons { b.isChecked = a == style.arrow }
            let s = arrow.layoutRows(); setFrameSize(s); return s
        case .text:
            textSwatch.color = style.color
            textSwatch.active = target == .color
            bgSwatch.color = style.textBackground
            bgSwatch.active = target == .background
            bgNone.isChecked = style.textBackground == nil
            bgNone.tip = Loc.t(style.textBackground == nil ? "Options.AddBackground" : "Options.NoBackground")
            for (a, b) in alignButtons { b.isChecked = a == style.textAlign }
            let s = text.layoutRows(); setFrameSize(s); return s
        default:
            setFrameSize(.zero); return .zero
        }
    }
}

/// Background frame options (Overlay/BeautifySection.cs).
final class BeautifySectionView: FlyoutSection {
    var optionsChanged: ((BeautifyOptions) -> Void)?
    private var presets: [BeautifyPreset: HudButton] = [:]
    private var paddings: [BeautifyPadding: HudButton] = [:]
    private var radii: [Int: HudButton] = [:]
    private let shadowButton = stripButton(title: "")
    private(set) var options = BeautifyOptions.default

    override init(frame: NSRect) {
        super.init(frame: frame)
        var items: [NSView] = []
        for p in BeautifyPreset.allCases {
            let b = HudButton(size: CGSize(width: 26, height: 26), icon: .custom { r, _ in
                let chip = CGRect(x: r.midX - 9, y: r.midY - 9, width: 18, height: 18)
                let (a, c) = BeautifyOptions.colors(p)
                NSGradient(starting: a.nsColor, ending: c.nsColor)?.draw(in: NSBezierPath(roundedRect: chip, xRadius: 5, yRadius: 5), angle: -45)
            })
            b.cornerRadius = 7
            b.action = { [weak self] in guard let self else { return }; var o = self.options; o.preset = p; self.raise(o) }
            presets[p] = b; items.append(b)
        }
        addRow("Beautify.Background", items)
        items = []
        for p in BeautifyPadding.allCases {
            let b = stripButton(title: p.rawValue, width: 30)
            b.action = { [weak self] in guard let self else { return }; var o = self.options; o.padding = p; self.raise(o) }
            paddings[p] = b; items.append(b)
        }
        addRow("Beautify.Padding", items)
        items = []
        for r in BeautifyOptions.radii {
            let b = stripButton(title: "\(r)", width: 30)
            b.action = { [weak self] in guard let self else { return }; var o = self.options; o.radius = r; self.raise(o) }
            radii[r] = b; items.append(b)
        }
        addRow("Beautify.Radius", items)
        shadowButton.action = { [weak self] in guard let self else { return }; var o = self.options; o.shadow.toggle(); self.raise(o) }
        addRow("Beautify.Shadow", [shadowButton])
    }
    required init?(coder: NSCoder) { fatalError() }

    @discardableResult func show(_ o: BeautifyOptions) -> CGSize {
        options = o
        for (p, b) in presets {
            b.tip = Loc.f("Beautify.BackgroundPreset", BeautifyOptions.name(p))
            b.showsRing = p == o.preset
        }
        for (p, b) in paddings { b.isChecked = p == o.padding; b.tip = Loc.f("Beautify.PaddingPixels", BeautifyOptions.paddingPixels(p)) }
        for (r, b) in radii { b.isChecked = r == o.radius; b.tip = r == 0 ? Loc.t("Beautify.NoRadius") : Loc.f("Beautify.RadiusPixels", r) }
        shadowButton.title = Loc.t("Beautify.Soft")
        shadowButton.tip = Loc.t("Beautify.SoftTip")
        shadowButton.isChecked = o.shadow
        return layoutRows()
    }

    private func raise(_ o: BeautifyOptions) {
        if o == options { show(options); return }
        optionsChanged?(o)
    }
}

extension HudButton {
    private static var ringKey = 0
    /// Accent ring (SwatchButton "Active" state) for preset chips.
    var showsRing: Bool {
        get { (objc_getAssociatedObject(self, &HudButton.ringKey) as? Bool) ?? false }
        set {
            objc_setAssociatedObject(self, &HudButton.ringKey, newValue, .OBJC_ASSOCIATION_RETAIN)
            wantsLayer = true
            layer?.cornerRadius = 7
            layer?.borderColor = Palette.accent.cgColor
            layer?.borderWidth = newValue ? 2 : 0
        }
    }
}

/// Flyout card hosting one section view.
final class FlyoutPanel: HudCard {
    let body: NSView
    var mouseInside: ((Bool) -> Void)?
    private(set) var isOpen = false

    init(body: NSView) {
        self.body = body
        super.init(frame: .zero)
        radius = 11
        addSubview(body)
        isHidden = true
    }
    required init?(coder: NSCoder) { fatalError() }

    func fit(_ content: CGSize) {
        body.frame = CGRect(x: 6, y: 5, width: content.width, height: content.height)
        setFrameSize(CGSize(width: content.width + 12, height: content.height + 10))
    }

    func open(at p: CGPoint) {
        setFrameOrigin(p)
        if !isOpen {
            isOpen = true
            isHidden = false
            alphaValue = 0
            NSAnimationContext.runAnimationGroup { $0.duration = 0.12; animator().alphaValue = 1 }
        }
        superview?.addSubview(self, positioned: .above, relativeTo: nil)
    }

    func close() {
        guard isOpen else { return }
        isOpen = false
        isHidden = true
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeAlways, .inVisibleRect], owner: self))
    }
    override func mouseEntered(with event: NSEvent) { mouseInside?(true) }
    override func mouseExited(with event: NSEvent) { mouseInside?(false) }
}

/// What the flyouts need to know from the editor (OptionsFlyouts.FlyoutState).
struct FlyoutState {
    var enabled = false
    var tool = ToolKind.select
    var section: OptionsSectionKind?
    var shape: Annotation?
    var shapeHidden = false
    var beautifyOn = false
}

protocol FlyoutHost: AnyObject {
    var flyoutLayer: NSView { get }
    var flyoutWorkArea: CGRect { get }
    var toolbarFlyoutsBelow: Bool { get }
    var toolbarFrame: CGRect { get }
    func shapeRect(_ a: Annotation) -> CGRect?
}

/// Opens the options flyout at the active tool's button (hover) or next to the selected shape, and the beautify
/// flyout at ✨ (Overlay/OptionsFlyouts.cs).
final class FlyoutController {
    static let hoverOpen = 0.15, hoverClose = 0.3, intro = 1.5
    let toolbar: ToolbarView
    let options = OptionsPanelView()
    let beautify = BeautifySectionView()
    lazy var optionsFlyout = FlyoutPanel(body: options)
    lazy var beautifyFlyout = FlyoutPanel(body: beautify)
    weak var host: FlyoutHost?
    private var state = FlyoutState()
    private var anchoredAtShape = false
    private var anchorTool: ToolKind?
    private var introduced = Set<ToolKind>()
    private var timers: [ObjectIdentifier: Timer] = [:]
    private var overAnchor: [ObjectIdentifier: Bool] = [:], overFlyout: [ObjectIdentifier: Bool] = [:]
    private var introTimer: Timer?

    init(toolbar: ToolbarView) {
        self.toolbar = toolbar
        for (kind, b) in toolbar.toolButtons {
            b.onHover = { [weak self] inside in self?.toolHover(kind, inside) }
        }
        toolbar.beautifyButton.onHover = { [weak self] inside in self?.beautifyHover(inside) }
        optionsFlyout.mouseInside = { [weak self] inside in self?.flyoutHover(self?.optionsFlyout, inside) }
        beautifyFlyout.mouseInside = { [weak self] inside in self?.flyoutHover(self?.beautifyFlyout, inside) }
    }

    func attach(_ h: FlyoutHost) {
        host = h
        h.flyoutLayer.addSubview(optionsFlyout)
        h.flyoutLayer.addSubview(beautifyFlyout)
    }

    var optionsAtShape: Bool { optionsFlyout.isOpen && anchoredAtShape }

    func contains(_ v: NSView?) -> Bool {
        var cur = v
        while let c = cur { if c === optionsFlyout || c === beautifyFlyout { return true }; cur = c.superview }
        return false
    }

    func update(_ s: FlyoutState) {
        state = s
        if !s.enabled { closeAll(); return }
        if !s.beautifyOn { beautifyFlyout.close() }
        if s.shape != nil && s.section != nil {
            if s.shapeHidden { optionsFlyout.close(); return }
            anchoredAtShape = true
            openOptionsAtShape()
            return
        }
        if anchoredAtShape || toolAnchor == nil || (anchorTool != nil && anchorTool != s.tool) {
            optionsFlyout.close()
            anchoredAtShape = false
            anchorTool = nil
        } else if optionsFlyout.isOpen {
            openOptionsAtTool() // section content may have changed size
        }
    }

    func onToolPicked(_ t: ToolKind) {
        guard toolAnchor != nil, state.tool == t else { return }
        if !introduced.insert(t).inserted {
            if toolbar.button(for: t)?.hovered == true { openOptionsAtTool() }
            return
        }
        openOptionsAtTool()
        startIntro(optionsFlyout)
    }

    func onBeautifyToggled(_ on: Bool) {
        guard on, beautifyAnchor != nil else { beautifyFlyout.close(); return }
        openBeautify()
        if !toolbar.beautifyButton.hovered { startIntro(beautifyFlyout) }
    }

    func closeAll() {
        timers.values.forEach { $0.invalidate() }; timers.removeAll()
        introTimer?.invalidate()
        optionsFlyout.close(); beautifyFlyout.close()
        anchoredAtShape = false; anchorTool = nil
    }

    func reposition() {
        if beautifyFlyout.isOpen { if beautifyAnchor != nil { openBeautify() } else { beautifyFlyout.close() } }
        if optionsFlyout.isOpen { if anchoredAtShape { openOptionsAtShape() } else if toolAnchor != nil { openOptionsAtTool() } else { optionsFlyout.close() } }
    }

    // Anchors

    private var beautifyAnchor: HudButton? {
        state.enabled && state.beautifyOn && !toolbar.beautifyButton.isHidden && !toolbar.isHidden ? toolbar.beautifyButton : nil
    }
    private var toolAnchor: HudButton? {
        guard state.enabled, !(state.shape != nil && state.section != nil), state.section != nil,
              let b = toolbar.button(for: state.tool), !b.isHidden, !toolbar.isHidden else { return nil }
        return b
    }

    func openBeautify() {
        guard let b = beautifyAnchor, let host else { return }
        beautifyFlyout.fit(beautify.show(beautify.options))
        place(beautifyFlyout, anchor: b.convert(b.bounds, to: host.flyoutLayer))
    }

    func openOptionsAtTool() {
        guard let b = toolAnchor, let host else { return }
        anchoredAtShape = false
        anchorTool = state.tool
        let size = options.frame.size
        if size == .zero { optionsFlyout.close(); return }
        optionsFlyout.fit(size)
        place(optionsFlyout, anchor: b.convert(b.bounds, to: host.flyoutLayer))
    }

    private func openOptionsAtShape() {
        guard let host, let shape = state.shape, let r = host.shapeRect(shape) else { optionsFlyout.close(); return }
        let size = options.frame.size
        if size == .zero { optionsFlyout.close(); return }
        optionsFlyout.fit(size)
        let (p, _) = HudLayout.placeFlyout(anchor: r.insetBy(dx: -4, dy: -4), size: optionsFlyout.frame.size, work: host.flyoutWorkArea,
                                           preferBelow: true, avoid: host.toolbarFrame)
        optionsFlyout.open(at: p)
    }

    private func place(_ f: FlyoutPanel, anchor: CGRect) {
        guard let host else { return }
        let (p, _) = HudLayout.placeFlyout(anchor: anchor, size: f.frame.size, work: host.flyoutWorkArea, preferBelow: host.toolbarFlyoutsBelow)
        f.open(at: p)
    }

    // Hover intent

    private func toolHover(_ kind: ToolKind, _ inside: Bool) {
        guard kind == state.tool, toolAnchor != nil else { return }
        let id = ObjectIdentifier(optionsFlyout)
        overAnchor[id] = inside
        if inside { endIntro(); schedule(id, Self.hoverOpen) { [weak self] in self?.openOptionsAtTool() } }
        else { scheduleClose(optionsFlyout) }
    }

    private func beautifyHover(_ inside: Bool) {
        guard beautifyAnchor != nil else { return }
        let id = ObjectIdentifier(beautifyFlyout)
        overAnchor[id] = inside
        if inside { endIntro(); schedule(id, Self.hoverOpen) { [weak self] in self?.openBeautify() } }
        else { scheduleClose(beautifyFlyout) }
    }

    private func flyoutHover(_ f: FlyoutPanel?, _ inside: Bool) {
        guard let f else { return }
        let id = ObjectIdentifier(f)
        overFlyout[id] = inside
        if inside { endIntro(); timers[id]?.invalidate() } else { scheduleClose(f) }
    }

    /// Decided from where the pointer really is when the timer fires (hidden views send no exit events).
    private func pointerOver(_ v: NSView?) -> Bool {
        guard let v, !v.isHidden, let w = v.window else { return false }
        return v.bounds.contains(v.convert(w.mouseLocationOutsideOfEventStream, from: nil))
    }

    private func hovering(_ f: FlyoutPanel) -> Bool {
        let anchor: NSView? = f === optionsFlyout ? toolbar.button(for: state.tool) : toolbar.beautifyButton
        return pointerOver(f) || pointerOver(anchor)
    }

    private func scheduleClose(_ f: FlyoutPanel) {
        let id = ObjectIdentifier(f)
        if f === optionsFlyout && anchoredAtShape { return } // shape options stay while the shape is selected
        schedule(id, Self.hoverClose) { [weak self, weak f] in
            guard let self, let f else { return }
            if self.hovering(f) { return }
            if f === self.optionsFlyout && self.anchoredAtShape { return }
            f.close()
        }
    }

    private func schedule(_ id: ObjectIdentifier, _ delay: Double, _ f: @escaping () -> Void) {
        timers[id]?.invalidate()
        timers[id] = Timer.scheduledTimer(withTimeInterval: delay, repeats: false) { _ in f() }
    }

    private func startIntro(_ f: FlyoutPanel) {
        introTimer?.invalidate()
        introTimer = Timer.scheduledTimer(withTimeInterval: Self.intro, repeats: false) { [weak self, weak f] _ in
            guard let self, let f else { return }
            if self.hovering(f) { return }
            if f === self.optionsFlyout && self.anchoredAtShape { return }
            f.close()
        }
    }

    private func endIntro() { introTimer?.invalidate(); introTimer = nil }
}


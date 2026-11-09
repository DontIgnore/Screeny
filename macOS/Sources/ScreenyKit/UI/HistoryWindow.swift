import AppKit
import ImageIO

/// Recent screenshots as a card grid (History/HistoryWindow.xaml.cs): click copies, double-click opens, E edits.
final class HistoryWindowController: NSWindowController, NSWindowDelegate {
    private let services: AppServices
    private let header = themeLabel("", color: Theme.ink70)
    private let folderText = themeLabel("", font: Theme.caption.withMono, color: Theme.ink50)
    private let emptyText = themeLabel("", font: .systemFont(ofSize: 15), color: Theme.ink50, wraps: true)
    private let emptyHint = themeLabel("", font: Theme.caption, color: Theme.ink70, wraps: true)
    private let emptyState = NSStackView()
    private let scroll = NSScrollView()
    private let grid = HistoryGridView()
    private let openFolderButton = ThemeButton("")
    private let clearButton = ThemeButton("", kind: .danger)
    private var generation = 0
    private var observers: [NSObjectProtocol] = []

    init(services: AppServices) {
        self.services = services
        let w = NSWindow(contentRect: CGRect(x: 0, y: 0, width: 920, height: 620),
                         styleMask: [.titled, .closable, .miniaturizable, .resizable], backing: .buffered, defer: false)
        w.minSize = CGSize(width: 480, height: 320)
        w.isReleasedWhenClosed = false
        Theme.style(w)
        w.center()
        super.init(window: w)
        w.delegate = self
        w.setFrameAutosaveName("HistoryWindow")
        buildUI()
        grid.owner = self
        let nc = NotificationCenter.default
        observers.append(nc.addObserver(forName: services.historyChanged, object: nil, queue: .main) { [weak self] _ in self?.refreshIfVisible() })
        observers.append(nc.addObserver(forName: Loc.changed, object: nil, queue: .main) { [weak self] _ in self?.refreshIfVisible() })
    }
    required init?(coder: NSCoder) { fatalError() }
    deinit { observers.forEach(NotificationCenter.default.removeObserver) }

    func show() {
        refresh()
        showWindow(nil)
        window?.makeKeyAndOrderFront(nil)
        window?.makeFirstResponder(grid)
        NSApp.activate()
    }

    func windowWillClose(_ notification: Notification) { generation += 1 }

    private func refreshIfVisible() { if window?.isVisible == true { refresh() } }

    private func buildUI() {
        guard let content = window?.contentView else { return }
        openFolderButton.handler = { [weak self] in self?.openFolder() }
        clearButton.handler = { [weak self] in self?.clearAll() }
        folderText.lineBreakMode = .byTruncatingMiddle
        folderText.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        header.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)

        let titles = NSStackView(views: [header, folderText])
        titles.orientation = .vertical
        titles.alignment = .leading
        titles.spacing = 2
        let buttons = NSStackView(views: [openFolderButton, clearButton])
        buttons.spacing = 8
        let top = NSStackView(views: [titles, buttons])
        top.distribution = .fill
        top.spacing = 16
        titles.setHuggingPriority(NSLayoutConstraint.Priority(1), for: .horizontal)
        buttons.setHuggingPriority(.required, for: .horizontal)

        scroll.drawsBackground = false
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.documentView = grid
        grid.frame = CGRect(x: 0, y: 0, width: 900, height: 10)

        emptyText.alignment = .center
        emptyHint.alignment = .center
        emptyState.orientation = .vertical
        emptyState.spacing = 8
        emptyState.addArrangedSubview(emptyText)
        emptyState.addArrangedSubview(emptyHint)
        emptyState.isHidden = true

        for v in [top, scroll, emptyState] as [NSView] { v.translatesAutoresizingMaskIntoConstraints = false; content.addSubview(v) }
        let titlebar = window.map { $0.frame.height - $0.contentLayoutRect.height } ?? 28
        NSLayoutConstraint.activate([
            top.topAnchor.constraint(equalTo: content.topAnchor, constant: titlebar + 4),
            top.leadingAnchor.constraint(equalTo: content.leadingAnchor, constant: 24),
            top.trailingAnchor.constraint(equalTo: content.trailingAnchor, constant: -24),
            scroll.topAnchor.constraint(equalTo: top.bottomAnchor, constant: 12),
            scroll.leadingAnchor.constraint(equalTo: content.leadingAnchor),
            scroll.trailingAnchor.constraint(equalTo: content.trailingAnchor),
            scroll.bottomAnchor.constraint(equalTo: content.bottomAnchor),
            emptyState.centerXAnchor.constraint(equalTo: scroll.centerXAnchor),
            emptyState.centerYAnchor.constraint(equalTo: scroll.centerYAnchor),
            emptyState.widthAnchor.constraint(lessThanOrEqualTo: scroll.widthAnchor, constant: -48),
        ])
        observers.append(NotificationCenter.default.addObserver(forName: NSView.frameDidChangeNotification, object: scroll.contentView, queue: .main) { [weak self] _ in
            self?.grid.relayout()
        })
        scroll.contentView.postsFrameChangedNotifications = true
    }

    // MARK: Loading

    func refresh() {
        generation += 1
        let gen = generation
        let store = services.history
        window?.title = Loc.t("History.Title")
        openFolderButton.title = Loc.t("History.OpenFolder")
        clearButton.title = Loc.t("History.ClearAll")
        openFolderButton.invalidateIntrinsicContentSize()
        clearButton.invalidateIntrinsicContentSize()
        folderText.stringValue = store.folder.path
        grid.toolTip = nil

        // HistoryStore.list() returns [] for an unreadable folder; tell that apart from an empty one.
        var isDir: ObjCBool = false
        if FileManager.default.fileExists(atPath: store.folder.path, isDirectory: &isDir),
           !isDir.boolValue || (try? FileManager.default.contentsOfDirectory(atPath: store.folder.path)) == nil {
            grid.setItems([])
            header.stringValue = Loc.t("History.FolderUnavailable")
            showEmpty(Loc.t("History.ReadFailed"), store.folder.path)
            return
        }
        let items = store.list().map(HistoryCard.init)
        grid.setItems(items)
        header.stringValue = Loc.f("History.Count", items.count, store.limit)
        if items.isEmpty {
            let hint = services.hotkeyText.map { Loc.f("History.EmptyHintHotkey", $0) } ?? Loc.t("History.EmptyHintTray")
            showEmpty(Loc.t("History.Empty"), hint)
        } else {
            emptyState.isHidden = true
        }
        loadThumbnails(items, gen)
    }

    private func showEmpty(_ message: String, _ hint: String) {
        emptyText.stringValue = message
        emptyHint.stringValue = hint
        emptyState.isHidden = false
    }

    /// Decodes small thumbnails one by one off the main thread; a newer refresh cancels the rest.
    private func loadThumbnails(_ cards: [HistoryCard], _ gen: Int) {
        let urls = cards.map(\.item.url)
        let maxPixels = Int(HistoryCard.thumbSize.width * 2)
        DispatchQueue.global(qos: .userInitiated).async { [weak self] in
            for (i, url) in urls.enumerated() {
                let thumb = Self.loadThumbnail(url, maxPixels: maxPixels)
                var stop = false
                DispatchQueue.main.sync {
                    guard let self, gen == self.generation else { stop = true; return }
                    cards[i].setThumbnail(thumb)
                }
                if stop { return }
            }
        }
    }

    private static func loadThumbnail(_ url: URL, maxPixels: Int) -> (CGImage?, Int, Int) {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, [kCGImageSourceShouldCache: false] as CFDictionary) else {
            Log.error("History thumbnail failed: \(url.path)")
            return (nil, 0, 0)
        }
        let props = CGImageSourceCopyPropertiesAtIndex(src, 0, nil) as? [CFString: Any]
        let w = props?[kCGImagePropertyPixelWidth] as? Int ?? 0, h = props?[kCGImagePropertyPixelHeight] as? Int ?? 0
        let opts: [CFString: Any] = [kCGImageSourceCreateThumbnailFromImageAlways: true,
                                     kCGImageSourceThumbnailMaxPixelSize: maxPixels,
                                     kCGImageSourceCreateThumbnailWithTransform: true]
        let thumb = CGImageSourceCreateThumbnailAtIndex(src, 0, opts as CFDictionary)
        if thumb == nil { Log.error("History thumbnail failed: \(url.path)") }
        return (thumb, w, h)
    }

    // MARK: Actions

    var canPin: Bool { services.settings.isOn(.pins) }

    func copy(_ card: HistoryCard) {
        guard let src = CGImageSourceCreateWithURL(card.item.url as CFURL, nil),
              let img = CGImageSourceCreateImageAtIndex(src, 0, nil) else {
            Log.error("History copy failed: \(card.item.url.path)")
            card.flash(Loc.t("History.Error"))
            return
        }
        card.flash(Loc.t(services.copyImage(img, scale: 1) ? "Common.Copied" : "History.ClipboardBusy"))
    }

    func open(_ card: HistoryCard) {
        if !NSWorkspace.shared.open(card.item.url) {
            Log.error("History open failed: \(card.item.url.path)")
            info(Loc.t("App.OpenShotFailedTitle"), Loc.t("History.OpenFailedText"))
        }
    }

    func edit(_ card: HistoryCard) { services.openEditor(card.item) }

    func pin(_ card: HistoryCard) {
        guard canPin, let src = CGImageSourceCreateWithURL(card.item.url as CFURL, nil),
              let img = CGImageSourceCreateImageAtIndex(src, 0, nil) else { return }
        services.pin(img, scale: 1)
    }

    func showInFinder(_ card: HistoryCard) { NSWorkspace.shared.activateFileViewerSelecting([card.item.url]) }

    /// Deleting skips the Trash, so ask first.
    func delete(_ card: HistoryCard) {
        confirm(Loc.t("History.DeleteTitle"), Loc.t("History.DeleteText"), Loc.t("Common.Delete")) { [weak self] in
            guard let self else { return }
            do { try self.services.history.delete(card.item.url) } catch {
                Log.error("History delete failed", error)
                self.info(Loc.t("History.DeleteFailedTitle"), Loc.t("History.DeleteFailedText"))
            }
            self.refresh()
        }
    }

    private func openFolder() {
        let folder = services.history.folder
        do { try FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true) } catch {
            Log.error("History folder create failed", error)
            info(Loc.t("History.CreateFolderFailed"), folder.path)
            return
        }
        NSWorkspace.shared.open(folder)
    }

    private func clearAll() {
        let store = services.history
        let snapshot = store.list()
        if snapshot.isEmpty { refresh(); return }
        confirm(Loc.t("History.ClearTitle"), Loc.f("History.ClearText", store.folder.path, snapshot.count), Loc.t("History.DeleteAll")) { [weak self] in
            // Only the set shown in the confirmation, even if new shots arrived meanwhile.
            for s in snapshot {
                do { try store.delete(s.url) } catch { Log.error("History clear failed", error) }
            }
            self?.refresh()
        }
    }

    private func confirm(_ title: String, _ text: String, _ button: String, then action: @escaping () -> Void) {
        guard let window else { return }
        let a = NSAlert()
        a.alertStyle = .warning
        a.messageText = title
        a.informativeText = text
        a.addButton(withTitle: button).hasDestructiveAction = true
        a.addButton(withTitle: Loc.t("Common.Cancel")).keyEquivalent = "\u{1b}"
        a.beginSheetModal(for: window) { if $0 == .alertFirstButtonReturn { action() } }
    }

    private func info(_ title: String, _ text: String) {
        guard let window else { return }
        let a = NSAlert()
        a.messageText = title
        a.informativeText = text
        a.addButton(withTitle: Loc.t("Common.OK"))
        a.beginSheetModal(for: window)
    }

    fileprivate func menu(for card: HistoryCard) -> NSMenu {
        let m = NSMenu()
        func add(_ title: String, _ key: String = "", _ mods: NSEvent.ModifierFlags = [], _ f: @escaping (HistoryCard) -> Void) {
            let i = BlockMenuItem(title: title, key: key) { [weak card] in if let card { f(card) } }
            i.keyEquivalentModifierMask = mods
            m.addItem(i)
        }
        add(Loc.t("History.Edit"), "e") { [weak self] in self?.edit($0) }
        add(Loc.t("Common.Copy"), "c", .command) { [weak self] in self?.copy($0) }
        if canPin { add(Loc.t("History.PinOnScreen")) { [weak self] in self?.pin($0) } }
        add(Loc.t("Common.Open"), "\r") { [weak self] in self?.open($0) }
        add(Loc.t("History.ShowInFolder")) { [weak self] in self?.showInFinder($0) }
        m.addItem(.separator())
        add(Loc.t("Common.Delete"), "\u{8}") { [weak self] in self?.delete($0) }
        return m
    }
}

private extension NSFont {
    var withMono: NSFont { .monospacedSystemFont(ofSize: pointSize, weight: .regular) }
}

private final class BlockMenuItem: NSMenuItem {
    private let block: () -> Void
    init(title: String, key: String, block: @escaping () -> Void) {
        self.block = block
        super.init(title: title, action: #selector(fire), keyEquivalent: key)
        target = self
    }
    required init(coder: NSCoder) { fatalError() }
    @objc private func fire() { block() }
}

// MARK: - Grid

/// Wrap panel of cards with single selection and keyboard navigation (the ListBox of the Windows window).
final class HistoryGridView: FlippedView {
    weak var owner: HistoryWindowController?
    private(set) var cards: [HistoryCard] = []
    private(set) var selected: Int? { didSet { cards.enumerated().forEach { $1.isSelected = $0 == selected } } }
    private var columns = 1
    static let padding: CGFloat = 18, margin: CGFloat = 6

    override var acceptsFirstResponder: Bool { true }
    override func becomeFirstResponder() -> Bool { cards.forEach { $0.needsDisplay = true }; return true }
    override func resignFirstResponder() -> Bool { cards.forEach { $0.needsDisplay = true }; return true }

    func setItems(_ new: [HistoryCard]) {
        let keep = selected.map { cards[$0].item.url }
        cards.forEach { $0.removeFromSuperview() }
        cards = new
        for c in new { c.grid = self; addSubview(c) }
        selected = keep.flatMap { u in new.firstIndex { $0.item.url == u } } ?? (new.isEmpty ? nil : 0)
        relayout()
    }

    func relayout() {
        guard let clip = enclosingScrollView?.contentView else { return }
        let width = clip.bounds.width
        let cell = CGSize(width: HistoryCard.size.width + 2 * Self.margin, height: HistoryCard.size.height + 2 * Self.margin)
        columns = max(1, Int((width - 2 * Self.padding) / cell.width))
        for (i, c) in cards.enumerated() {
            c.baseOrigin = CGPoint(x: Self.padding + CGFloat(i % columns) * cell.width + Self.margin,
                                   y: CGFloat(i / columns) * cell.height + Self.margin)
        }
        let rows = (cards.count + columns - 1) / columns
        setFrameSize(CGSize(width: width, height: max(clip.bounds.height, CGFloat(rows) * cell.height + Self.padding)))
    }

    var hasFocus: Bool { window?.isKeyWindow == true && window?.firstResponder === self }

    func select(_ card: HistoryCard) {
        selected = cards.firstIndex { $0 === card }
        window?.makeFirstResponder(self)
    }

    private var current: HistoryCard? { selected.map { cards[$0] } }

    private func move(_ delta: Int) {
        guard !cards.isEmpty else { return }
        let i = max(0, min(cards.count - 1, (selected ?? 0) + delta))
        selected = i
        cards[i].scrollToVisible(cards[i].bounds.insetBy(dx: 0, dy: -Self.margin))
    }

    override func keyDown(with event: NSEvent) {
        let mods = event.modifierFlags.intersection([.command, .option, .control, .shift])
        switch Int(event.keyCode) {
        case 123: move(-1)
        case 124: move(1)
        case 126: move(-columns)
        case 125: move(columns)
        case 36, 76: if let c = current { owner?.open(c) }
        case 51, 117: if let c = current { owner?.delete(c) }
        case 53: window?.performClose(nil)
        default:
            if mods.isEmpty, event.charactersIgnoringModifiers?.lowercased() == "e", let c = current { owner?.edit(c) }
            else { super.keyDown(with: event) }
        }
    }

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        if event.modifierFlags.intersection([.command, .option, .control, .shift]) == .command,
           event.charactersIgnoringModifiers?.lowercased() == "c", window?.firstResponder === self, let c = current {
            owner?.copy(c)
            return true
        }
        return super.performKeyEquivalent(with: event)
    }

    @objc func copy(_ sender: Any?) { if let c = current { owner?.copy(c) } }
}

/// One screenshot card: thumbnail on `Theme.thumb`, date and size under it, action buttons on hover.
final class HistoryCard: FlippedView {
    static let thumbSize = CGSize(width: 200, height: 130)
    static let size = CGSize(width: 216, height: 8 + 130 + 8 + 16 + 8)

    let item: HistoryItem
    weak var grid: HistoryGridView?
    var isSelected = false { didSet { needsDisplay = true } }
    var baseOrigin = CGPoint.zero { didSet { updateOrigin() } }
    private var thumbnail: CGImage?
    private var sizeText = ""
    private var status: String?
    private var statusTimer: Timer?
    private var hovered = false
    private let actions = NSStackView()

    private static let dateFormatter: DateFormatter = {
        let f = DateFormatter()
        f.dateStyle = .short
        f.timeStyle = .medium
        return f
    }()

    init(item: HistoryItem) {
        self.item = item
        super.init(frame: CGRect(origin: .zero, size: Self.size))
        toolTip = Loc.t("History.CardTip")
        actions.spacing = 0
        actions.edgeInsets = NSEdgeInsets(top: 2, left: 2, bottom: 2, right: 2)
        actions.wantsLayer = true
        actions.layer?.cornerRadius = 8
        actions.layer?.borderWidth = 1
        actions.isHidden = true
        addSubview(actions)
    }
    required init?(coder: NSCoder) { fatalError() }

    func setThumbnail(_ t: (CGImage?, Int, Int)) {
        thumbnail = t.0
        sizeText = t.1 > 0 ? "\(t.1)×\(t.2)" : ""
        needsDisplay = true
    }

    func flash(_ text: String) {
        status = text
        needsDisplay = true
        statusTimer?.invalidate()
        statusTimer = Timer.scheduledTimer(withTimeInterval: 1.2, repeats: false) { [weak self] _ in self?.status = nil; self?.needsDisplay = true }
    }

    private func updateOrigin() { setFrameOrigin(CGPoint(x: baseOrigin.x, y: baseOrigin.y - (hovered ? 2 : 0))) }

    private var thumbRect: CGRect { CGRect(x: 8, y: 8, width: Self.thumbSize.width, height: Self.thumbSize.height) }

    private func buildActions() {
        actions.arrangedSubviews.forEach { $0.removeFromSuperview() }
        var list: [(String, String, (HistoryCard) -> Void)] = [
            ("pencil", Loc.t("History.Edit"), { $0.grid?.owner?.edit($0) }),
            ("doc.on.doc", Loc.t("Common.Copy"), { $0.grid?.owner?.copy($0) }),
        ]
        if grid?.owner?.canPin == true { list.append(("pin", Loc.t("History.PinOnScreen"), { $0.grid?.owner?.pin($0) })) }
        list.append(("trash", Loc.t("Common.Delete"), { $0.grid?.owner?.delete($0) }))
        for (symbol, tip, f) in list {
            let b = ThemeButton("", kind: .subtle) { [weak self] in if let self { f(self) } }
            b.image = NSImage(systemSymbolName: symbol, accessibilityDescription: tip)
            b.toolTip = tip
            b.translatesAutoresizingMaskIntoConstraints = false
            b.widthAnchor.constraint(equalToConstant: 28).isActive = true
            b.heightAnchor.constraint(equalToConstant: 28).isActive = true
            actions.addArrangedSubview(b)
        }
        effectiveAppearance.performAsCurrentDrawingAppearance {
            actions.layer?.backgroundColor = Theme.popup.cgColor
            actions.layer?.borderColor = Theme.stroke.cgColor
        }
        let s = actions.fittingSize
        actions.frame = CGRect(x: thumbRect.maxX - 6 - s.width, y: thumbRect.minY + 6, width: s.width, height: s.height)
    }

    override func updateTrackingAreas() {
        super.updateTrackingAreas()
        trackingAreas.forEach(removeTrackingArea)
        addTrackingArea(NSTrackingArea(rect: bounds, options: [.mouseEnteredAndExited, .activeInKeyWindow, .inVisibleRect], owner: self))
    }
    override func mouseEntered(with event: NSEvent) {
        hovered = true
        buildActions()
        actions.isHidden = false
        updateOrigin(); needsDisplay = true
    }
    override func mouseExited(with event: NSEvent) {
        hovered = false
        actions.isHidden = true
        updateOrigin(); needsDisplay = true
    }
    override func resetCursorRects() { addCursorRect(bounds, cursor: .pointingHand) }

    override func mouseDown(with event: NSEvent) { grid?.select(self) }
    override func mouseUp(with event: NSEvent) {
        guard bounds.contains(convert(event.locationInWindow, from: nil)) else { return }
        if event.clickCount == 2 { grid?.owner?.open(self) } else if event.clickCount == 1 { grid?.owner?.copy(self) }
    }
    override func rightMouseDown(with event: NSEvent) {
        grid?.select(self)
        if let m = grid?.owner?.menu(for: self) { NSMenu.popUpContextMenu(m, with: event, for: self) }
    }

    override func draw(_ dirtyRect: NSRect) {
        let focused = isSelected && grid?.hasFocus == true
        let lw: CGFloat = focused ? 2 : 1
        let card = NSBezierPath(roundedRect: bounds.insetBy(dx: lw / 2, dy: lw / 2), xRadius: 12, yRadius: 12)
        (hovered ? Theme.cardHover : Theme.card).setFill()
        card.fill()
        (isSelected ? Theme.accent : Theme.stroke).setStroke()
        card.lineWidth = lw
        card.stroke()

        let thumbPath = NSBezierPath(roundedRect: thumbRect.insetBy(dx: 0.5, dy: 0.5), xRadius: 8, yRadius: 8)
        Theme.thumb.setFill(); thumbPath.fill()
        Theme.stroke.setStroke(); thumbPath.stroke()
        if let t = thumbnail, let ctx = NSGraphicsContext.current?.cgContext {
            let area = thumbRect.insetBy(dx: 1, dy: 1)
            let k = min(area.width / CGFloat(t.width), area.height / CGFloat(t.height))
            let sz = CGSize(width: CGFloat(t.width) * k, height: CGFloat(t.height) * k)
            let r = CGRect(x: area.midX - sz.width / 2, y: area.midY - sz.height / 2, width: sz.width, height: sz.height)
            ctx.saveGState()
            thumbPath.addClip()
            ctx.interpolationQuality = .high
            ctx.translateBy(x: 0, y: r.maxY + r.minY)
            ctx.scaleBy(x: 1, y: -1)
            ctx.draw(t, in: r)
            ctx.restoreGState()
        }

        let mono = NSFont.monospacedSystemFont(ofSize: 12, weight: .regular)
        let y = thumbRect.maxY + 8
        let size = NSAttributedString(string: sizeText, attributes: [.font: mono, .foregroundColor: Theme.ink50])
        let sw = size.size().width
        size.draw(at: CGPoint(x: bounds.maxX - 10 - sw, y: y))
        NSAttributedString(string: Self.dateFormatter.string(from: item.createdAt), attributes: [.font: mono, .foregroundColor: Theme.ink])
            .draw(with: CGRect(x: 10, y: y, width: bounds.width - 28 - sw, height: 16), options: [.usesLineFragmentOrigin, .truncatesLastVisibleLine])

        if let status {
            let s = NSAttributedString(string: status, attributes: [.font: NSFont.systemFont(ofSize: 12, weight: .semibold), .foregroundColor: NSColor.white])
            let ts = s.size()
            let pill = CGRect(x: thumbRect.midX - ts.width / 2 - 10, y: thumbRect.midY - ts.height / 2 - 4, width: ts.width + 20, height: ts.height + 8)
            Theme.accent.setFill()
            NSBezierPath(roundedRect: pill, xRadius: 6, yRadius: 6).fill()
            s.draw(at: CGPoint(x: pill.minX + 10, y: pill.minY + 4))
        }
    }
}

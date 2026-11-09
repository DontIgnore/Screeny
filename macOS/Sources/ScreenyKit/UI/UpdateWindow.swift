import AppKit

/// "Version X is available" (Updates/UpdateWindow.xaml): what's new, then open the release page, later, or skip this
/// version. Nothing is installed here: the Mac app is updated by downloading the new version from GitHub.
final class UpdateWindowController: NSWindowController, NSWindowDelegate {
    let release: ReleaseInfo
    private let notes: [NoteBlock]
    private let current: String?
    private let skip: () -> Bool
    var onClose: (() -> Void)?

    private static let width: CGFloat = 520, inner: CGFloat = 472
    private let heading = themeLabel("", font: Theme.h1, wraps: true)
    private let currentText = themeLabel("", font: Theme.caption, color: Theme.ink70)
    private let whatsNew = themeLabel("", font: Theme.sectionLabel, color: Theme.ink70)
    private let notesView = NSTextView()
    private let errorText = themeLabel("", font: Theme.caption, color: Theme.dangerText, wraps: true)
    private let skipButton = ThemeButton("")
    private let laterButton = ThemeButton("")
    private let openButton = ThemeButton("", kind: .accent)

    /// - Parameter skip: remembers the skipped version; false if saving failed.
    init(release: ReleaseInfo, notes: [NoteBlock], current: String?, skip: @escaping () -> Bool) {
        self.release = release
        self.notes = notes
        self.current = current
        self.skip = skip
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: Self.width, height: 400), styleMask: [.titled, .closable], backing: .buffered, defer: true)
        Theme.style(w)
        w.isReleasedWhenClosed = false
        super.init(window: w)
        w.delegate = self
        build()
        render()
        NotificationCenter.default.addObserver(self, selector: #selector(render), name: Loc.changed, object: nil)
    }

    required init?(coder: NSCoder) { fatalError() }
    deinit { NotificationCenter.default.removeObserver(self) }

    func show(activate: Bool) {
        if window?.isVisible != true { window?.center() }
        if activate { NSApp.activate(ignoringOtherApps: true); showWindow(nil) } else { window?.orderFrontRegardless() }
    }

    private func build() {
        heading.preferredMaxLayoutWidth = Self.inner
        errorText.preferredMaxLayoutWidth = Self.inner
        errorText.isHidden = true
        currentText.isHidden = current == nil

        notesView.isEditable = false
        notesView.drawsBackground = false
        notesView.textContainerInset = NSSize(width: 12, height: 12)
        notesView.textStorage?.setAttributedString(Self.attributed(notes))
        notesView.isVerticallyResizable = true
        notesView.autoresizingMask = [.width]
        notesView.frame = NSRect(x: 0, y: 0, width: Self.inner, height: 10)
        notesView.textContainer?.widthTracksTextView = true
        notesView.layoutManager?.ensureLayout(for: notesView.textContainer!)
        let textHeight = (notesView.layoutManager?.usedRect(for: notesView.textContainer!).height ?? 100) + 26
        let scroll = NSScrollView()
        scroll.hasVerticalScroller = true
        scroll.autohidesScrollers = true
        scroll.drawsBackground = true
        scroll.backgroundColor = Theme.card
        scroll.documentView = notesView
        scroll.wantsLayer = true
        scroll.layer?.cornerRadius = 8
        scroll.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            scroll.widthAnchor.constraint(equalToConstant: Self.inner),
            scroll.heightAnchor.constraint(equalToConstant: min(300, ceil(textHeight))),
        ])

        let body = NSStackView(views: [heading, currentText, whatsNew, scroll, errorText])
        body.orientation = .vertical
        body.alignment = .leading
        body.spacing = 8
        body.setCustomSpacing(4, after: heading)
        body.setCustomSpacing(20, after: currentText)
        body.setCustomSpacing(12, after: scroll)
        body.edgeInsets = NSEdgeInsets(top: 4, left: 24, bottom: 20, right: 24)

        skipButton.handler = { [weak self] in self?.skipVersion() }
        laterButton.handler = { [weak self] in self?.close() }
        laterButton.keyEquivalent = "\u{1b}"
        openButton.handler = { [weak self] in
            guard let self else { return }
            NSWorkspace.shared.open(self.release.pageURL)
            self.close()
        }
        openButton.keyEquivalent = "\r"
        let buttons = NSStackView(views: [skipButton, NSView(), laterButton, openButton])
        buttons.spacing = 8
        buttons.edgeInsets = NSEdgeInsets(top: 16, left: 24, bottom: 20, right: 24)
        let content = NSView()
        dialogLayout(content, body: body, buttons: buttons, width: Self.width)
        window?.contentView = content
    }

    @objc private func render() {
        window?.title = Loc.t("Update.Title")
        heading.stringValue = Loc.f("Update.Available", release.version)
        currentText.stringValue = current.map { Loc.f("Update.Current", $0) } ?? ""
        whatsNew.stringValue = Loc.t("Update.WhatsNew").uppercased()
        skipButton.title = Loc.t("Update.SkipVersion")
        laterButton.title = Loc.t("Update.Later")
        openButton.title = Loc.t("Update.UpdateAndRestart")
        if notes.isEmpty { notesView.textStorage?.setAttributedString(Self.attributed(notes)) }
        [skipButton, laterButton, openButton].forEach { $0.invalidateIntrinsicContentSize() }
        refit()
    }

    private func refit() {
        guard let w = window, let content = w.contentView else { return }
        content.layoutSubtreeIfNeeded()
        var frame = w.frameRect(forContentRect: NSRect(origin: .zero, size: NSSize(width: Self.width, height: content.fittingSize.height)))
        frame.origin = NSPoint(x: w.frame.minX, y: w.frame.maxY - frame.height)
        w.setFrame(frame, display: true)
    }

    private func skipVersion() {
        if skip() { close(); return }
        errorText.stringValue = Loc.t("Update.SkipFailed")
        errorText.isHidden = false
        refit()
    }

    func windowWillClose(_ notification: Notification) { onClose?() }

    /// Notes as rich text: headings, bullet/numbered items with hanging indent, bold, code. Spacing as in the XAML.
    static func attributed(_ blocks: [NoteBlock]) -> NSAttributedString {
        let out = NSMutableAttributedString()
        if blocks.isEmpty {
            out.append(NSAttributedString(string: Loc.t("Update.NoNotes"), attributes: [.font: Theme.body, .foregroundColor: Theme.ink50]))
            return out
        }
        func isList(_ b: NoteBlock) -> Bool { b.kind == .bullet || b.kind == .numbered }
        for (i, b) in blocks.enumerated() {
            let heading = b.kind == .heading1 || b.kind == .heading2 || b.kind == .heading3
            let font: NSFont = b.kind == .heading1 ? .systemFont(ofSize: 16, weight: .semibold)
                : b.kind == .heading2 ? .systemFont(ofSize: 14, weight: .semibold)
                : b.kind == .heading3 ? .systemFont(ofSize: 13, weight: .semibold) : Theme.body
            let color = heading ? Theme.ink : Theme.ink70
            let p = NSMutableParagraphStyle()
            p.minimumLineHeight = 20
            p.paragraphSpacingBefore = i == 0 ? 0 : heading ? 14 : (i > 0 && isList(blocks[i - 1]) && isList(b)) ? 4 : 8
            var line = NSMutableAttributedString()
            if isList(b) {
                let indent = CGFloat(b.level) * 20
                p.firstLineHeadIndent = indent
                p.headIndent = indent + 20
                p.tabStops = [NSTextTab(textAlignment: .left, location: indent + 20)]
                line = NSMutableAttributedString(string: (b.kind == .numbered ? "\(b.number)." : "•") + "\t",
                                                 attributes: [.font: Theme.body, .foregroundColor: Theme.ink50])
            }
            for x in b.inlines {
                var f = font
                if x.code { f = .monospacedSystemFont(ofSize: 12, weight: .regular) }
                if x.bold { f = NSFontManager.shared.convert(f, toHaveTrait: .boldFontMask) }
                line.append(NSAttributedString(string: x.text, attributes: [.font: f, .foregroundColor: x.bold ? Theme.ink : color]))
            }
            if i < blocks.count - 1 { line.append(NSAttributedString(string: "\n")) }
            line.addAttribute(.paragraphStyle, value: p, range: NSRange(location: 0, length: line.length))
            out.append(line)
        }
        return out
    }
}

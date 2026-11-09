import AppKit

/// Bug report or idea without an account (Feedback/FeedbackWindow.xaml): title, description, optional contact and, for
/// a bug, technical details the user can look at before sending. Field errors appear after the first send attempt; on
/// failure the text stays in the fields. Texts are rebuilt when the language changes.
final class FeedbackWindowController: NSWindowController, NSWindowDelegate, NSTextFieldDelegate, NSTextViewDelegate {
    private let services: AppServices
    let kind: FeedbackKind
    /// Called when the window closes (the app keeps one feedback window and forgets it here).
    var onClose: (() -> Void)?

    private var diagnosticsCache: FeedbackDiagnostics?
    private var diagnostics: FeedbackDiagnostics { diagnosticsCache ?? { let d = FeedbackDiagnosticsCollector.collect(); diagnosticsCache = d; return d }() }
    private var task: URLSessionTask?
    private var sending = false
    private var result: FeedbackResult?
    private var validate = false
    private var detailsShown = false

    private static let width: CGFloat = 480, inner: CGFloat = 432

    private let heading = themeLabel("", font: Theme.h1, wraps: true)
    private let intro = themeLabel("", font: Theme.caption, color: Theme.ink70, wraps: true)
    private let form = NSStackView()
    private let titleLabel = themeLabel("")
    private let titleInput = NSTextField()
    private let titleError = FeedbackWindowController.errorLabel()
    private let descriptionLabel = themeLabel("")
    private let descriptionInput = NSTextView()
    private let descriptionPlaceholder = themeLabel("", color: Theme.ink50, wraps: true)
    private let descriptionError = FeedbackWindowController.errorLabel()
    private let contactLabel = themeLabel("")
    private let contactInput = NSTextField()
    private let contactError = FeedbackWindowController.errorLabel()
    private let contactNote = themeLabel("", font: Theme.caption, color: Theme.ink70, wraps: true)
    private let detailsPanel = NSStackView()
    private let attachDetails = NSButton(checkboxWithTitle: "", target: nil, action: nil)
    private let detailsToggle = NSButton(title: "", target: nil, action: nil)
    private let detailsPreview = NSTextView()
    private var detailsScroll: NSScrollView!
    private let privacy = themeLabel("", font: Theme.caption, color: Theme.ink70, wraps: true)
    private let sendingPanel = NSStackView()
    private let progress = NSProgressIndicator()
    private let sendingLabel = themeLabel("", font: Theme.caption, color: Theme.ink70)
    private let errorPanel = NSView()
    private let errorText = themeLabel("", color: Theme.dangerText, wraps: true)
    private let successPanel = NSStackView()
    private let successText = themeLabel("", font: NSFont.systemFont(ofSize: 13, weight: .semibold), wraps: true)
    private let successNote = themeLabel("", font: Theme.caption, color: Theme.ink70, wraps: true)
    private let gitHubLink = NSButton(title: "", target: nil, action: nil)
    private let cancelButton = ThemeButton("")
    private let sendButton = ThemeButton("", kind: .accent)
    private let closeButton = ThemeButton("", kind: .accent)

    init(services: AppServices, bug: Bool) {
        self.services = services
        kind = bug ? .bug : .idea
        let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: Self.width, height: 600), styleMask: [.titled, .closable, .miniaturizable], backing: .buffered, defer: true)
        Theme.style(w)
        w.isReleasedWhenClosed = false
        super.init(window: w)
        w.delegate = self
        build()
        render()
        NotificationCenter.default.addObserver(self, selector: #selector(languageChanged), name: Loc.changed, object: nil)
    }

    required init?(coder: NSCoder) { fatalError() }
    deinit { NotificationCenter.default.removeObserver(self) }

    func show() {
        NSApp.activate(ignoringOtherApps: true)
        if window?.isVisible != true { refit(); window?.center() }
        showWindow(nil)
        window?.makeFirstResponder(titleInput)
    }

    // MARK: Layout

    private static func errorLabel() -> NSTextField {
        let l = themeLabel("", font: Theme.caption, color: Theme.dangerText, wraps: true)
        l.isHidden = true
        return l
    }

    private func linkStyle(_ b: NSButton, _ action: Selector) {
        b.isBordered = false
        b.target = self
        b.action = action
        b.setContentHuggingPriority(.required, for: .horizontal)
    }

    private func setLinkTitle(_ b: NSButton, _ title: String) {
        b.attributedTitle = NSAttributedString(string: title, attributes: [.font: Theme.caption, .foregroundColor: b.isEnabled ? Theme.accent : Theme.ink50])
    }

    private func scroll(_ tv: NSTextView, height: CGFloat) -> NSScrollView {
        let s = NSScrollView()
        s.hasVerticalScroller = true
        s.autohidesScrollers = true
        s.borderType = .noBorder
        s.drawsBackground = true
        s.backgroundColor = Theme.card
        s.wantsLayer = true
        s.layer?.cornerRadius = 8
        s.layer?.borderWidth = 1
        s.layer?.borderColor = Theme.strokeStrong.cgColor
        tv.isRichText = false
        tv.allowsUndo = true
        tv.drawsBackground = false
        tv.textColor = Theme.ink
        tv.insertionPointColor = Theme.ink
        tv.textContainerInset = NSSize(width: 6, height: 7)
        tv.isVerticallyResizable = true
        tv.autoresizingMask = [.width]
        tv.textContainer?.widthTracksTextView = true
        s.documentView = tv
        s.translatesAutoresizingMaskIntoConstraints = false
        s.heightAnchor.constraint(equalToConstant: height).isActive = true
        s.widthAnchor.constraint(equalToConstant: Self.inner).isActive = true
        return s
    }

    private func field(_ f: NSTextField) {
        f.font = Theme.body
        f.delegate = self
        f.translatesAutoresizingMaskIntoConstraints = false
        f.widthAnchor.constraint(equalToConstant: Self.inner).isActive = true
    }

    private func vstack(_ s: NSStackView, _ views: [NSView], spacing: CGFloat = 6) {
        s.orientation = .vertical
        s.alignment = .leading
        s.spacing = spacing
        views.forEach(s.addArrangedSubview)
    }

    private func build() {
        for l in [heading, intro, titleError, descriptionError, contactError, contactNote, privacy, errorText, successText, successNote] {
            l.preferredMaxLayoutWidth = Self.inner
        }
        errorText.preferredMaxLayoutWidth = Self.inner - 24
        field(titleInput)
        field(contactInput)

        descriptionInput.font = Theme.body
        descriptionInput.delegate = self
        let descriptionScroll = scroll(descriptionInput, height: 128)
        descriptionPlaceholder.translatesAutoresizingMaskIntoConstraints = false
        descriptionInput.addSubview(descriptionPlaceholder)
        descriptionPlaceholder.preferredMaxLayoutWidth = Self.inner - 22
        NSLayoutConstraint.activate([
            descriptionPlaceholder.leadingAnchor.constraint(equalTo: descriptionInput.leadingAnchor, constant: 11),
            descriptionPlaceholder.widthAnchor.constraint(equalToConstant: Self.inner - 22),
            descriptionPlaceholder.topAnchor.constraint(equalTo: descriptionInput.topAnchor, constant: 7),
        ])

        // Details (bugs only): checkbox, "Show what's sent" link, and the exact text that goes out.
        attachDetails.state = .on
        attachDetails.font = Theme.body
        attachDetails.target = self
        attachDetails.action = #selector(attachChanged)
        linkStyle(detailsToggle, #selector(toggleDetails))
        let detailsRow = NSStackView(views: [attachDetails, NSView(), detailsToggle])
        detailsRow.translatesAutoresizingMaskIntoConstraints = false
        detailsRow.widthAnchor.constraint(equalToConstant: Self.inner).isActive = true
        detailsPreview.font = NSFont.monospacedSystemFont(ofSize: 11, weight: .regular)
        detailsPreview.isEditable = false
        detailsScroll = scroll(detailsPreview, height: 150)
        detailsScroll.isHidden = true
        vstack(detailsPanel, [detailsRow, detailsScroll], spacing: 8)
        detailsPanel.isHidden = kind != .bug

        vstack(form, [titleLabel, titleInput, titleError, descriptionLabel, descriptionScroll, descriptionError,
                      contactLabel, contactInput, contactError, contactNote, detailsPanel, privacy])
        for v in [titleLabel, descriptionLabel, contactLabel, detailsPanel, privacy] {
            if let prev = form.arrangedSubviews.firstIndex(of: v).flatMap({ $0 > 0 ? form.arrangedSubviews[$0 - 1] : nil }) {
                form.setCustomSpacing(v === titleLabel || v === descriptionLabel || v === contactLabel ? 14 : 16, after: prev)
            }
        }
        form.setCustomSpacing(14, after: titleError)
        form.setCustomSpacing(14, after: descriptionError)

        progress.style = .bar
        progress.isIndeterminate = true
        progress.controlSize = .small
        progress.translatesAutoresizingMaskIntoConstraints = false
        progress.widthAnchor.constraint(equalToConstant: Self.inner).isActive = true
        vstack(sendingPanel, [progress, sendingLabel], spacing: 8)
        sendingPanel.isHidden = true

        errorPanel.wantsLayer = true
        errorPanel.layer?.cornerRadius = 8
        errorText.translatesAutoresizingMaskIntoConstraints = false
        errorPanel.addSubview(errorText)
        errorPanel.translatesAutoresizingMaskIntoConstraints = false
        NSLayoutConstraint.activate([
            errorPanel.widthAnchor.constraint(equalToConstant: Self.inner),
            errorText.leadingAnchor.constraint(equalTo: errorPanel.leadingAnchor, constant: 12),
            errorText.trailingAnchor.constraint(equalTo: errorPanel.trailingAnchor, constant: -12),
            errorText.topAnchor.constraint(equalTo: errorPanel.topAnchor, constant: 10),
            errorText.bottomAnchor.constraint(equalTo: errorPanel.bottomAnchor, constant: -10),
        ])
        errorPanel.isHidden = true

        let check = NSImageView(image: NSImage(systemSymbolName: "checkmark.circle.fill", accessibilityDescription: nil)!
            .withSymbolConfiguration(.init(pointSize: 36, weight: .regular))!)
        check.contentTintColor = Theme.accent
        vstack(successPanel, [check, successText, successNote], spacing: 4)
        successPanel.setCustomSpacing(14, after: check)
        successPanel.isHidden = true

        let body = NSStackView()
        vstack(body, [heading, intro, form, sendingPanel, errorPanel, successPanel], spacing: 16)
        body.setCustomSpacing(4, after: heading)
        body.edgeInsets = NSEdgeInsets(top: 4, left: 24, bottom: 20, right: 24)

        // Bottom bar: GitHub link on the left, Cancel and Send on the right; after sending only Close.
        linkStyle(gitHubLink, #selector(openGitHub))
        cancelButton.handler = { [weak self] in self?.close() }
        cancelButton.keyEquivalent = "\u{1b}"
        sendButton.handler = { [weak self] in self?.send() }
        sendButton.keyEquivalent = "\r"
        closeButton.handler = { [weak self] in self?.close() }
        closeButton.isHidden = true
        let buttons = NSStackView(views: [gitHubLink, NSView(), cancelButton, sendButton, closeButton])
        buttons.spacing = 8
        buttons.edgeInsets = NSEdgeInsets(top: 14, left: 24, bottom: 18, right: 24)
        let content = LayerColors()
        content.apply = { [weak self] in
            guard let self else { return }
            self.errorPanel.layer?.backgroundColor = Theme.dangerSubtle.cgColor
            for s in [self.detailsScroll, self.descriptionInput.enclosingScrollView] { s?.layer?.borderColor = Theme.strokeStrong.cgColor }
        }
        dialogLayout(content, body: body, buttons: buttons, width: Self.width)
        window?.contentView = content
        content.viewDidChangeEffectiveAppearance()
    }

    /// Window height follows the content; the top edge stays put.
    private func refit() {
        guard let w = window, let content = w.contentView else { return }
        content.layoutSubtreeIfNeeded()
        let size = NSSize(width: Self.width, height: content.fittingSize.height)
        var frame = w.frameRect(forContentRect: NSRect(origin: .zero, size: size))
        frame.origin = NSPoint(x: w.frame.minX, y: w.frame.maxY - frame.height)
        w.setFrame(frame, display: true, animate: false)
    }

    // MARK: Texts

    private func key(_ bug: String, _ idea: String) -> String { kind == .bug ? bug : idea }

    @objc private func languageChanged() { render(); refit() }

    private func render() {
        let title = Loc.t(key("Feedback.BugTitle", "Feedback.IdeaTitle"))
        window?.title = title
        heading.stringValue = title
        intro.stringValue = Loc.t(key("Feedback.BugIntro", "Feedback.IdeaIntro"))
        titleLabel.stringValue = Loc.t("Feedback.TitleLabel")
        titleInput.placeholderString = Loc.t(key("Feedback.BugTitlePlaceholder", "Feedback.IdeaTitlePlaceholder"))
        descriptionLabel.stringValue = Loc.t("Feedback.DescriptionLabel")
        descriptionPlaceholder.stringValue = Loc.t(key("Feedback.BugDescriptionPlaceholder", "Feedback.IdeaDescriptionPlaceholder"))
        contactLabel.stringValue = Loc.t("Feedback.ContactLabel")
        contactInput.placeholderString = Loc.t("Feedback.ContactPlaceholder")
        contactNote.stringValue = Loc.t("Feedback.ContactNote")
        titleInput.setAccessibilityLabel(titleLabel.stringValue)
        descriptionInput.setAccessibilityLabel(descriptionLabel.stringValue)
        contactInput.setAccessibilityLabel(contactLabel.stringValue)
        attachDetails.title = Loc.t("Feedback.AttachDetails")
        setLinkTitle(detailsToggle, Loc.t(detailsShown ? "Feedback.HideDetails" : "Feedback.ShowDetails"))
        if detailsShown { detailsPreview.string = Self.previewText(diagnostics) }
        privacy.stringValue = Loc.t("Feedback.Privacy")
        sendingLabel.stringValue = Loc.t("Feedback.Sending")
        successNote.stringValue = Loc.t("Feedback.SuccessNote")
        setLinkTitle(gitHubLink, Loc.t("Feedback.OpenGitHub"))
        cancelButton.title = Loc.t("Common.Cancel")
        sendButton.title = Loc.t("Feedback.Send")
        closeButton.title = Loc.t("Common.Close")
        [cancelButton, sendButton, closeButton].forEach { $0.invalidateIntrinsicContentSize() }
        updatePlaceholder()
        showValidation()
        showResult()
    }

    private func updatePlaceholder() { descriptionPlaceholder.isHidden = !descriptionInput.string.isEmpty }

    /// Everything that goes into `diagnostics`, line by line: labels in the UI language, values as they are.
    static func previewText(_ d: FeedbackDiagnostics) -> String {
        var lines = [
            "\(Loc.t("Feedback.DiagAppVersion")): \(d.appVersion)",
            "\(Loc.t("Feedback.DiagOs")): \(d.osVersion)",
            "\(Loc.t("Feedback.DiagLanguage")): \(d.uiLanguage)",
            "\(Loc.t("Feedback.DiagDpi")): \(d.dpi)",
            "\(Loc.t("Feedback.DiagMonitors")): \(d.monitors)",
            "",
            Loc.f("Feedback.DiagLog", d.logTail.isEmpty ? 0 : d.logTail.components(separatedBy: "\n").count),
        ]
        if !d.logTail.isEmpty { lines.append(d.logTail) }
        return lines.joined(separator: "\n")
    }

    /// Send error text in the current language; nil when there is none.
    static func errorMessage(_ r: FeedbackResult?) -> String? {
        switch r {
        case .offline: return Loc.t("Feedback.ErrorOffline")
        case .rateLimited(let t):
            return FeedbackClient.retryMinutes(t).map { Loc.f("Feedback.ErrorRateLimitedMinutes", $0) } ?? Loc.t("Feedback.ErrorRateLimited")
        case .invalid(let m?): return Loc.f("Feedback.ErrorInvalid", m)
        case .invalid(nil): return Loc.t("Feedback.ErrorInvalidNoMessage")
        case .serverError(let s), .failed(let s): return Loc.f("Feedback.ErrorServer", s)
        case .notConfigured: return Loc.t("Feedback.DevNotConfigured")
        case .sent, nil: return nil
        }
    }

    // MARK: Validation

    func controlTextDidChange(_ obj: Notification) { fieldChanged() }
    func textDidChange(_ notification: Notification) { updatePlaceholder(); fieldChanged() }

    private func fieldChanged() {
        if validate { let before = fieldErrorsShown; showValidation(); if before != fieldErrorsShown { refit() } }
    }

    private var fieldErrorsShown: [Bool] { [titleError, descriptionError, contactError].map { !$0.isHidden } }

    /// Returns true when all fields are fine.
    @discardableResult private func showValidation() -> Bool {
        let t = FeedbackRules.checkTitle(titleInput.stringValue)
        let d = FeedbackRules.checkDescription(descriptionInput.string)
        let c = FeedbackRules.checkContact(contactInput.stringValue)
        showFieldError(titleError, t, FeedbackRules.titleMin, FeedbackRules.titleMax)
        showFieldError(descriptionError, d, FeedbackRules.descriptionMin, FeedbackRules.descriptionMax)
        showFieldError(contactError, c, 0, FeedbackRules.contactMax)
        return t == nil && d == nil && c == nil
    }

    private func showFieldError(_ label: NSTextField, _ e: FeedbackFieldError?, _ min: Int, _ max: Int) {
        let show = validate && e != nil
        label.stringValue = !show ? "" : e == .tooShort ? Loc.f("Feedback.TooShort", min) : Loc.f("Feedback.TooLong", max)
        label.isHidden = !show
    }

    // MARK: Sending

    private func send() {
        if sending { return }
        if case .sent = result { return }
        validate = true
        if !showValidation() {
            refit()
            window?.makeFirstResponder(!titleError.isHidden ? titleInput : !descriptionError.isHidden ? descriptionInput : contactInput)
            return
        }
        let endpoint = FeedbackEndpoint.current()
        // installId only for a real send: a dev run without an endpoint neither creates it nor writes settings.
        let installId: String
        if endpoint == nil { installId = "" } else if let id = services.settings.installId { installId = id } else {
            installId = UUID().uuidString.lowercased()
            services.settings.installId = installId
            services.saveSettings()
        }
        let request = FeedbackRequest(kind: kind, title: titleInput.stringValue, description: descriptionInput.string, contact: contactInput.stringValue,
                                      diagnostics: kind == .bug && attachDetails.state == .on ? diagnostics : nil, installId: installId)
        result = nil
        setSending(true)
        task = FeedbackClient.send(request, to: endpoint) { [weak self] r in
            guard let self, self.window?.isVisible == true else { return }
            self.task = nil
            self.result = r
            self.setSending(false)
            self.showResult()
            self.refit()
        }
    }

    private func setSending(_ on: Bool) {
        sending = on
        for c in [titleInput, contactInput, attachDetails] as [NSControl] { c.isEnabled = !on }
        descriptionInput.isEditable = !on
        detailsToggle.isEnabled = !on && attachDetails.state == .on
        sendButton.isEnabled = !on
        gitHubLink.isEnabled = !on
        setLinkTitle(gitHubLink, Loc.t("Feedback.OpenGitHub"))
        setLinkTitle(detailsToggle, Loc.t(detailsShown ? "Feedback.HideDetails" : "Feedback.ShowDetails"))
        sendingPanel.isHidden = !on
        if on { errorPanel.isHidden = true; progress.startAnimation(nil) } else { progress.stopAnimation(nil) }
        refit()
    }

    /// Success replaces the form; an error is a line above the buttons and the fields keep their text.
    private func showResult() {
        var sent = false
        if case .sent(let id) = result {
            sent = true
            successText.stringValue = id.map { Loc.f("Feedback.Sent", $0) } ?? Loc.t("Feedback.SentNoId")
        }
        form.isHidden = sent
        intro.isHidden = sent
        successPanel.isHidden = !sent
        sendButton.isHidden = sent; cancelButton.isHidden = sent; gitHubLink.isHidden = sent
        closeButton.isHidden = !sent
        closeButton.keyEquivalent = sent ? "\r" : ""
        let error = Self.errorMessage(result)
        errorText.stringValue = error ?? ""
        errorPanel.isHidden = error == nil || sending
    }

    // MARK: Actions

    @objc private func toggleDetails() {
        detailsShown.toggle()
        detailsScroll.isHidden = !detailsShown
        render()
        refit()
    }

    @objc private func attachChanged() {
        detailsToggle.isEnabled = attachDetails.state == .on
        if attachDetails.state != .on && detailsShown { toggleDetails() } else { render() }
    }

    @objc private func openGitHub() {
        NSWorkspace.shared.open(GitHubIssueLink.build(kind, title: titleInput.stringValue))
    }

    func windowWillClose(_ notification: Notification) {
        task?.cancel()
        task = nil
        onClose?()
    }
}

/// Standard dialog layout: body under the title bar, a separator, the button row at the bottom; height from content.
func dialogLayout(_ content: NSView, body: NSView, buttons: NSView, width: CGFloat) {
    let separator = NSBox()
    separator.boxType = .separator
    for v in [body, separator, buttons] { v.translatesAutoresizingMaskIntoConstraints = false; content.addSubview(v) }
    NSLayoutConstraint.activate([
        content.widthAnchor.constraint(equalToConstant: width),
        body.topAnchor.constraint(equalTo: content.topAnchor, constant: 28),
        separator.topAnchor.constraint(equalTo: body.bottomAnchor),
        buttons.topAnchor.constraint(equalTo: separator.bottomAnchor),
        buttons.bottomAnchor.constraint(equalTo: content.bottomAnchor),
    ] + [body, separator, buttons].flatMap { [$0.leadingAnchor.constraint(equalTo: content.leadingAnchor), $0.trailingAnchor.constraint(equalTo: content.trailingAnchor)] })
}

/// Layer colors don't follow light/dark switches by themselves: re-resolve them here.
private final class LayerColors: NSView {
    var apply: (() -> Void)?
    override func viewDidChangeEffectiveAppearance() {
        super.viewDidChangeEffectiveAppearance()
        effectiveAppearance.performAsCurrentDrawingAppearance { apply?() }
    }
}

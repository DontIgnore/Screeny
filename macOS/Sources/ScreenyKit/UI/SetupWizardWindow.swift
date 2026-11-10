import AppKit
import CoreGraphics

/// First-run wizard (Migration/SetupWizardWindow.xaml). Windows looks for screenshot apps that steal its keys; on a Mac
/// the obstacle is the Screen Recording permission, so that card takes their place. Then shortcuts, features, updates and
/// open at login. The language applies at once (for reading the rest); everything else is saved by "Done".
/// "Skip" (or closing the window) keeps the old settings and the old language.
final class SetupWizardWindowController: NSWindowController, NSWindowDelegate {
    private let services: AppServices
    private let onFinish: () -> Void
    private var settings: AppSettings { services.settings }
    private let scroll = FormScrollView()
    private let editor: ShortcutListEditor
    private var features: [Feature: Bool]
    private var checkUpdates: Bool
    private var openAtLogin: Bool
    private let originalLanguage: String
    private var finished = false
    private var timer: Timer?
    private var observer: NSObjectProtocol?
    private var permissionGranted: Bool?
    private var permissionBox: NSStackView?, loginError: NSTextField?, editorView: NSView?
    private let skipButton = ThemeButton("")
    private let doneButton = ThemeButton("", kind: .accent)

    init(services: AppServices, onFinish: @escaping () -> Void) {
        self.services = services
        self.onFinish = onFinish
        let s = services.settings
        editor = ShortcutListEditor(s.hotkeys)
        features = Dictionary(uniqueKeysWithValues: Feature.allCases.map { ($0, s.isOn($0)) })
        checkUpdates = s.checkForUpdates
        openAtLogin = LoginItem.isOn
        originalLanguage = s.language
        let w = NSWindow(contentRect: CGRect(x: 0, y: 0, width: 620, height: 640), styleMask: [.titled, .closable],
                         backing: .buffered, defer: true)
        Theme.style(w)
        w.isReleasedWhenClosed = false
        super.init(window: w)
        w.delegate = self

        // Bottom bar: always visible, the content above scrolls.
        let bar = NSView()
        bar.translatesAutoresizingMaskIntoConstraints = false
        let line = NSBox()
        line.boxType = .custom
        line.borderWidth = 0
        line.fillColor = Theme.stroke
        let buttons = NSStackView(views: [skipButton, doneButton])
        buttons.spacing = 8
        for v in [line, buttons] as [NSView] { v.translatesAutoresizingMaskIntoConstraints = false; bar.addSubview(v) }
        skipButton.handler = { [unowned self] in finish(done: false) }
        doneButton.handler = { [unowned self] in finish(done: true) }
        doneButton.keyEquivalent = "\r"
        let root = NSView()
        for v in [scroll, bar] { root.addSubview(v) }
        NSLayoutConstraint.activate([
            scroll.topAnchor.constraint(equalTo: root.topAnchor),
            scroll.leadingAnchor.constraint(equalTo: root.leadingAnchor),
            scroll.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            scroll.bottomAnchor.constraint(equalTo: bar.topAnchor),
            bar.leadingAnchor.constraint(equalTo: root.leadingAnchor),
            bar.trailingAnchor.constraint(equalTo: root.trailingAnchor),
            bar.bottomAnchor.constraint(equalTo: root.bottomAnchor),
            line.topAnchor.constraint(equalTo: bar.topAnchor),
            line.leadingAnchor.constraint(equalTo: bar.leadingAnchor),
            line.trailingAnchor.constraint(equalTo: bar.trailingAnchor),
            line.heightAnchor.constraint(equalToConstant: 1),
            buttons.topAnchor.constraint(equalTo: bar.topAnchor, constant: 16),
            buttons.bottomAnchor.constraint(equalTo: bar.bottomAnchor, constant: -20),
            buttons.trailingAnchor.constraint(equalTo: bar.trailingAnchor, constant: -24),
            doneButton.widthAnchor.constraint(greaterThanOrEqualToConstant: 112),
            skipButton.widthAnchor.constraint(greaterThanOrEqualToConstant: 104),
        ])
        w.contentView = root
        observer = NotificationCenter.default.addObserver(forName: Loc.changed, object: nil, queue: .main) { [weak self] _ in self?.build() }
        build()
    }

    required init?(coder: NSCoder) { fatalError() }
    deinit { observer.map(NotificationCenter.default.removeObserver) }

    func show() {
        guard let w = window else { return }
        if !w.isVisible {
            let maxH = min(820, (w.screen ?? NSScreen.main)?.visibleFrame.height ?? 820)
            w.setContentSize(CGSize(width: 620, height: min(scroll.contentHeight + 69, maxH)))
            w.center()
            timer = Timer.scheduledTimer(withTimeInterval: 1, repeats: true) { [weak self] _ in self?.showPermission() }
        }
        NSApp.activate(ignoringOtherApps: true)
        w.makeKeyAndOrderFront(nil)
    }

    func windowWillClose(_ notification: Notification) {
        timer?.invalidate()
        timer = nil
        if !finished { finish(done: false) } // the close button is "Skip", as on Windows
    }

    private func build() {
        window?.title = Loc.t("Wizard.Title")
        skipButton.title = Loc.t("Wizard.Skip")
        doneButton.title = Loc.t("Wizard.Done")
        skipButton.invalidateIntrinsicContentSize()
        doneButton.invalidateIntrinsicContentSize()
        let col = Form.stack()

        // Language next to the heading: it's picked before reading the rest.
        let heading = themeLabel(Loc.t("Wizard.HeadingReady"), font: Theme.h1, wraps: true)
        let popup = makeLanguagePopup(settings.language) { [unowned self] code in
            settings.language = code
            services.saveSettings() // the app switches Loc; Loc.changed rebuilds this window
        }
        let head = NSStackView(views: [heading, popup])
        head.spacing = 16
        head.alignment = .centerY
        heading.setContentCompressionResistancePriority(.defaultLow, for: .horizontal)
        heading.setContentHuggingPriority(.defaultLow, for: .horizontal)
        Form.add(head, to: col, top: 0)
        Form.add(themeLabel(Loc.t("Mac.Wizard.Intro"), color: Theme.ink70, wraps: true), to: col, top: 8)

        Form.add(Form.caption("Mac.Wizard.SectionPermission"), to: col, top: 28)
        let perm = Form.stack()
        permissionBox = perm
        permissionGranted = nil
        Form.add(perm, to: col, top: 10)
        showPermission()

        Form.add(Form.caption("Wizard.SectionHotkeys"), to: col, top: 28)
        editor.changed = nil
        editorView = Form.add(editor.makeView(), to: col, top: 10)
        Form.add(Form.note(Loc.t("Mac.Wizard.ShortcutNote")), to: col, top: 10)

        Form.add(Form.caption("Settings.SectionFeatures"), to: col, top: 28)
        Form.add(Form.note(Loc.t("Wizard.FeaturesNote")), to: col, top: 8)
        for f in Feature.allCases {
            Form.add(CheckRow(f.title, on: features[f] ?? true) { [unowned self] on in features[f] = on }, to: col, top: 12)
            Form.add(Form.note(f.details), to: col, top: 4, indent: Form.checkIndent)
        }

        Form.add(Form.caption("Wizard.SectionUpdates"), to: col, top: 28)
        Form.add(CheckRow(Loc.t("Common.CheckUpdatesBox"), on: checkUpdates) { [unowned self] on in checkUpdates = on }, to: col, top: 10)
        Form.add(Form.note(Loc.t("Mac.Settings.PrivacyNote")), to: col, top: 4, indent: Form.checkIndent)

        Form.add(Form.caption("Settings.SectionBehavior"), to: col, top: 28)
        Form.add(CheckRow(Loc.t("Mac.Settings.OpenAtLogin"), on: openAtLogin) { [unowned self] on in openAtLogin = on }, to: col, top: 10)
        loginError = Form.error("")
        Form.add(loginError!, to: col, top: 4, indent: Form.checkIndent).isHidden = true

        scroll.setContent(col, top: 4)
    }

    /// Polled every second: the user grants access in System Settings, not here.
    private func showPermission() {
        let granted = CGPreflightScreenCaptureAccess()
        guard granted != permissionGranted, let box = permissionBox else { return }
        permissionGranted = granted
        box.arrangedSubviews.forEach { $0.removeFromSuperview() }
        if granted {
            Form.add(themeLabel(Loc.t("Mac.Wizard.PermissionGranted"), color: .systemGreen, wraps: true), to: box, top: 0)
            return
        }
        Form.add(themeLabel(Loc.t("Mac.Wizard.PermissionNeeded"), wraps: true), to: box, top: 0)
        Form.add(ThemeButton(Loc.t("Mac.Wizard.GrantAccess"), kind: .accent) {
            CGRequestScreenCaptureAccess()
            openSystemSettings("x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")
        }, to: box, top: 10, fill: false)
        Form.add(Form.note(Loc.t("Mac.Wizard.PermissionNote")), to: box, top: 8)
    }

    private func finish(done: Bool) {
        if done {
            let r = editor.result
            guard r.isValid else { editorView?.scrollToVisible(editorView?.bounds ?? .zero); return }
            if openAtLogin != LoginItem.isOn, let error = LoginItem.set(openAtLogin) {
                loginError?.stringValue = error
                loginError?.superview?.isHidden = false
                loginError?.scrollToVisible(loginError?.bounds ?? .zero)
                openAtLogin = LoginItem.isOn
                return
            }
            settings.hotkeys = r.hotkeys
            for (f, on) in features { settings.set(f, on: on) }
            settings.checkForUpdates = checkUpdates
            settings.startAtLogin = LoginItem.isOn
        } else {
            settings.language = originalLanguage
        }
        finished = true
        settings.setupWizardVersion = max(settings.setupWizardVersion, 1)
        services.saveSettings()
        close()
        onFinish()
    }
}

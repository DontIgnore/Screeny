import AppKit
import ServiceManagement

// MARK: - Shortcut list (shared by Settings and the setup wizard)

/// One line of the shortcut editor. `spec == nil` is an empty box: skipped, like an empty custom row on Windows.
struct ShortcutRow: Equatable {
    var spec: HotkeySpec?
    var enabled = true
}

/// Checks the shortcut list (SettingsWindow.CollectHotkeys): invalid keys and repeats get an error under their row,
/// empty rows are skipped, and at least one shortcut must stay on.
enum ShortcutRules {
    struct Result: Equatable {
        var hotkeys: [HotkeySpec]
        var rowErrors: [Int: String]
        var listError: String?
        var isValid: Bool { rowErrors.isEmpty && listError == nil }
    }

    static func check(_ rows: [ShortcutRow]) -> Result {
        var seen = Set<HotkeySpec>(), list: [HotkeySpec] = [], errors: [Int: String] = [:]
        for (i, row) in rows.enumerated() {
            guard var spec = row.spec else { continue }
            spec.enabled = row.enabled
            let key = HotkeySpec(keyCode: spec.keyCode, modifiers: spec.modifiers)
            if let e = spec.validationError ?? (seen.insert(key).inserted ? nil : Loc.t("Settings.DuplicateShortcut")) {
                errors[i] = e
                continue
            }
            list.append(spec)
        }
        let needOne = errors.isEmpty && !list.contains { $0.enabled } ? Loc.t("Mac.Settings.NeedOne") : nil
        return Result(hotkeys: list, rowErrors: errors, listError: needOne)
    }
}

/// Shortcut rows: enable box, recorder, remove button, errors and the "macOS screenshot shortcut" note under each.
final class ShortcutListEditor {
    private(set) var rows: [ShortcutRow]
    /// After every edit, with the checked list.
    var changed: ((ShortcutRules.Result) -> Void)?
    private var stack = NSStackView()

    init(_ hotkeys: [HotkeySpec]) { rows = hotkeys.map { ShortcutRow(spec: $0, enabled: $0.enabled) } }

    var result: ShortcutRules.Result { ShortcutRules.check(rows) }

    /// A fresh view (windows rebuild their content when the language changes); the rows survive.
    func makeView() -> NSView {
        stack = NSStackView()
        stack.orientation = .vertical
        stack.alignment = .leading
        stack.spacing = 0
        render()
        return stack
    }

    private func commit() {
        // Async: the recorder that reported the change finishes its own key handling before its view is replaced.
        DispatchQueue.main.async { [self] in
            render()
            changed?(result)
        }
    }

    private func render() {
        stack.arrangedSubviews.forEach { $0.removeFromSuperview() }
        let res = result
        for (i, row) in rows.enumerated() {
            let box = NSButton(checkboxWithTitle: "", target: nil, action: nil)
            box.state = row.enabled ? .on : .off
            box.setAccessibilityLabel(Loc.t("Hotkeys.CustomName"))
            box.whenClicked { [unowned self, unowned box] in rows[i].enabled = box.state == .on; commit() }
            let rec = HotkeyRecorder()
            rec.spec = row.spec
            rec.toolTip = Loc.t("Hotkeys.BoxTip")
            rec.setAccessibilityLabel(Loc.t("Hotkeys.CustomName"))
            rec.changed = { [unowned self] s in rows[i].spec = s; commit() }
            let remove = ThemeButton("✕", kind: .subtle) { [unowned self] in rows.remove(at: i); commit() }
            remove.toolTip = Loc.t("Settings.RemoveShortcut")
            remove.setAccessibilityLabel(Loc.t("Settings.RemoveShortcut"))
            let line = NSStackView(views: [box, rec, remove])
            line.spacing = 8
            Form.add(line, to: stack, top: i == 0 ? 0 : 8, fill: false)
            if let e = res.rowErrors[i] { Form.add(Form.error(e), to: stack, top: 4, indent: 26) }
            if let s = row.spec, s.isSystemScreenshotShortcut {
                Form.add(Form.note(Loc.f("Mac.Settings.SystemShortcut", s.displayText)), to: stack, top: 4, indent: 26)
                Form.add(Form.link(Loc.t("Mac.Settings.OpenKeyboardSettings")) {
                    openSystemSettings("x-apple.systempreferences:com.apple.Keyboard-Settings.extension")
                }, to: stack, top: 4, indent: 26, fill: false)
            }
        }
        let add = ThemeButton("+  " + Loc.t("Settings.Add")) { [unowned self] in rows.append(ShortcutRow()); commit() }
        add.toolTip = Loc.t("Settings.AddShortcutTip")
        Form.add(add, to: stack, top: rows.isEmpty ? 0 : 10, fill: false)
        if let e = res.listError { Form.add(Form.error(e), to: stack, top: 6) }
    }
}

func openSystemSettings(_ url: String) {
    if let u = URL(string: url), NSWorkspace.shared.open(u) { return }
    Log.error("Open System Settings failed: \(url)")
}

/// Open at login through SMAppService. Returns nil or a localized error.
enum LoginItem {
    static var isOn: Bool { SMAppService.mainApp.status == .enabled }

    static func set(_ on: Bool) -> String? {
        do {
            if on { try SMAppService.mainApp.register() } else { try SMAppService.mainApp.unregister() }
        } catch {
            Log.error("Login item change failed", error)
            return Loc.f("Mac.Settings.LoginItemFailed", error.localizedDescription)
        }
        return on && SMAppService.mainApp.status == .requiresApproval ? Loc.t("Mac.Settings.LoginItemApproval") : nil
    }
}

// MARK: - Layout helpers

/// Single-column form pieces in the window style (Theme.xaml: SectionLabel, Caption, ErrorText, Hyperlink).
enum Form {
    static let checkIndent: CGFloat = 24

    static func stack() -> NSStackView {
        let s = NSStackView()
        s.orientation = .vertical
        s.alignment = .leading
        s.spacing = 0
        return s
    }

    /// Adds `view` with its top gap and indent baked into a wrapper, so hidden rows don't leave stray spacing.
    @discardableResult
    static func add(_ view: NSView, to stack: NSStackView, top: CGFloat = 12, indent: CGFloat = 0, fill: Bool = true) -> NSView {
        let wrap = NSView()
        wrap.translatesAutoresizingMaskIntoConstraints = false
        view.translatesAutoresizingMaskIntoConstraints = false
        wrap.addSubview(view)
        NSLayoutConstraint.activate([
            view.topAnchor.constraint(equalTo: wrap.topAnchor, constant: top),
            view.bottomAnchor.constraint(equalTo: wrap.bottomAnchor),
            view.leadingAnchor.constraint(equalTo: wrap.leadingAnchor, constant: indent),
            fill ? view.trailingAnchor.constraint(equalTo: wrap.trailingAnchor)
                 : view.trailingAnchor.constraint(lessThanOrEqualTo: wrap.trailingAnchor),
        ])
        stack.addArrangedSubview(wrap)
        wrap.widthAnchor.constraint(equalTo: stack.widthAnchor).isActive = true
        return wrap
    }

    static func caption(_ key: String) -> NSTextField {
        themeLabel(Loc.t(key).uppercased(), font: Theme.sectionLabel, color: Theme.ink50)
    }

    static func note(_ text: String) -> NSTextField { themeLabel(text, font: Theme.caption, color: Theme.ink70, wraps: true) }

    static func error(_ text: String) -> NSTextField { themeLabel(text, font: Theme.caption, color: Theme.dangerText, wraps: true) }

    static func link(_ title: String, _ action: @escaping () -> Void) -> NSButton {
        let b = NSButton(title: "", target: nil, action: nil)
        b.isBordered = false
        b.attributedTitle = NSAttributedString(string: title, attributes: [.font: Theme.caption, .foregroundColor: Theme.accent])
        b.whenClicked(action)
        return b
    }
}

/// Checkbox whose title wraps (long texts and translations don't fit one line); clicking the text toggles it too.
final class CheckRow: NSView {
    let box = NSButton(checkboxWithTitle: "", target: nil, action: nil)
    var isOn: Bool {
        get { box.state == .on }
        set { box.state = newValue ? .on : .off }
    }

    init(_ title: String, on: Bool, _ changed: @escaping (Bool) -> Void) {
        super.init(frame: .zero)
        let label = themeLabel(title, wraps: true)
        box.state = on ? .on : .off
        box.setAccessibilityLabel(title)
        box.whenClicked { [unowned box] in changed(box.state == .on) }
        label.addGestureRecognizer(NSClickGestureRecognizer(target: box, action: #selector(NSButton.performClick(_:))))
        for v in [box, label] as [NSView] { v.translatesAutoresizingMaskIntoConstraints = false; addSubview(v) }
        NSLayoutConstraint.activate([
            box.leadingAnchor.constraint(equalTo: leadingAnchor),
            box.firstBaselineAnchor.constraint(equalTo: label.firstBaselineAnchor),
            label.leadingAnchor.constraint(equalTo: leadingAnchor, constant: Form.checkIndent),
            label.trailingAnchor.constraint(equalTo: trailingAnchor),
            label.topAnchor.constraint(equalTo: topAnchor),
            label.bottomAnchor.constraint(equalTo: bottomAnchor),
        ])
    }

    required init?(coder: NSCoder) { fatalError() }
}

private final class ClosureTarget: NSObject {
    let f: () -> Void
    init(_ f: @escaping () -> Void) { self.f = f }
    @objc func fire() { f() }
}

private var closureTargetKey = 0

extension NSControl {
    func whenClicked(_ f: @escaping () -> Void) {
        let t = ClosureTarget(f)
        objc_setAssociatedObject(self, &closureTargetKey, t, .OBJC_ASSOCIATION_RETAIN)
        target = t
        action = #selector(ClosureTarget.fire)
    }
}


/// Scrollable single column with 24 pt margins; `setContent` swaps the column (rebuild on language change).
final class FormScrollView: NSScrollView {
    private let doc = FlippedView()

    init() {
        super.init(frame: .zero)
        hasVerticalScroller = true
        autohidesScrollers = true
        drawsBackground = false
        translatesAutoresizingMaskIntoConstraints = false
        doc.translatesAutoresizingMaskIntoConstraints = false
        documentView = doc
        NSLayoutConstraint.activate([
            doc.leadingAnchor.constraint(equalTo: contentView.leadingAnchor),
            doc.trailingAnchor.constraint(equalTo: contentView.trailingAnchor),
            doc.topAnchor.constraint(equalTo: contentView.topAnchor),
        ])
    }

    required init?(coder: NSCoder) { fatalError() }

    func setContent(_ v: NSView, top: CGFloat = 8) {
        doc.subviews.forEach { $0.removeFromSuperview() }
        v.translatesAutoresizingMaskIntoConstraints = false
        doc.addSubview(v)
        NSLayoutConstraint.activate([
            v.leadingAnchor.constraint(equalTo: doc.leadingAnchor, constant: 24),
            v.trailingAnchor.constraint(equalTo: doc.trailingAnchor, constant: -24),
            v.topAnchor.constraint(equalTo: doc.topAnchor, constant: top),
            v.bottomAnchor.constraint(equalTo: doc.bottomAnchor, constant: -24),
        ])
    }

    var contentHeight: CGFloat { layoutSubtreeIfNeeded(); return doc.fittingSize.height }
}

/// Language popup: "Same as macOS (…)" and the five languages (Localization/LanguagePicker.cs).
func makeLanguagePopup(_ setting: String, _ picked: @escaping (String) -> Void) -> NSPopUpButton {
    let p = NSPopUpButton(frame: .zero, pullsDown: false)
    p.setAccessibilityLabel(Loc.t("Language.Name"))
    for code in [Loc.auto] + Loc.languages {
        p.addItem(withTitle: code == Loc.auto ? Loc.f("Mac.Settings.LanguageSystem", Loc.nativeName(Loc.resolve(Loc.auto))) : Loc.nativeName(code))
        p.lastItem?.representedObject = code
    }
    p.selectItem(at: ([Loc.auto] + Loc.languages).firstIndex(of: Loc.normalizeSetting(setting)) ?? 0)
    p.whenClicked { [unowned p] in if let c = p.selectedItem?.representedObject as? String { picked(c) } }
    p.widthAnchor.constraint(equalToConstant: 240).isActive = true
    return p
}

// MARK: - Settings window

/// Settings (Settings/SettingsWindow.xaml + .About.cs). Unlike Windows there is no Save/Cancel: like other Mac apps every
/// change applies and saves at once; a field with an error is simply not saved until it's fixed.
final class SettingsWindowController: NSWindowController, NSWindowDelegate {
    private let services: AppServices
    private var settings: AppSettings { services.settings }
    private let scroll = FormScrollView()
    private var editor: ShortcutListEditor
    private var observer: NSObjectProtocol?
    // Shown or hidden by the feature boxes (SettingsWindow.ShowFeatureFields).
    private var historySection: NSView?, enterCopiesRow: NSView?, excludePinsRow: NSView?
    private var folderField: NSTextField?, limitField: NSTextField?
    private var folderError: NSTextField?, limitError: NSTextField?, loginError: NSTextField?

    init(services: AppServices) {
        self.services = services
        editor = ShortcutListEditor(services.settings.hotkeys)
        let w = NSWindow(contentRect: CGRect(x: 0, y: 0, width: 560, height: 600), styleMask: [.titled, .closable, .miniaturizable],
                         backing: .buffered, defer: true)
        Theme.style(w)
        w.isReleasedWhenClosed = false
        super.init(window: w)
        w.delegate = self
        w.contentView = scroll
        observer = NotificationCenter.default.addObserver(forName: Loc.changed, object: nil, queue: .main) { [weak self] _ in self?.build() }
        build()
    }

    required init?(coder: NSCoder) { fatalError() }
    deinit { observer.map(NotificationCenter.default.removeObserver) }

    func show() {
        guard let w = window else { return }
        if !w.isVisible {
            // Settings may have changed elsewhere (wizard, menu): show what is saved now.
            editor = ShortcutListEditor(settings.hotkeys)
            build()
            let maxH = min(780, (w.screen ?? NSScreen.main)?.visibleFrame.height ?? 780)
            w.setContentSize(CGSize(width: 560, height: min(scroll.contentHeight, maxH)))
            w.center()
        }
        NSApp.activate(ignoringOtherApps: true)
        w.makeKeyAndOrderFront(nil)
    }

    func windowWillClose(_ notification: Notification) {
        // Typed but not confirmed with Return: saved like a lost focus.
        if let f = folderField { folderChanged(f) }
        if let f = limitField { limitChanged(f) }
    }

    private func save() { services.saveSettings() }

    private func build() {
        window?.title = Loc.t("Settings.Title")
        let col = Form.stack()

        // Language first: whoever can't read the current language still finds it.
        Form.add(Form.caption("Language.Section"), to: col, top: 0)
        Form.add(makeLanguagePopup(settings.language) { [unowned self] code in
            settings.language = code
            save()
        }, to: col, top: 10, fill: false)

        Form.add(Form.caption("Settings.SectionCapture"), to: col, top: 28)
        editor.changed = { [unowned self] r in
            guard r.isValid, r.hotkeys != settings.hotkeys else { return }
            settings.hotkeys = r.hotkeys
            save()
        }
        Form.add(editor.makeView(), to: col, top: 10)
        Form.add(ThemeButton(Loc.t("Mac.Settings.SetupWizard")) { [unowned self] in services.showSetupWizard() }, to: col, top: 12, fill: false)

        Form.add(Form.caption("Settings.SectionFeatures"), to: col, top: 28)
        for (i, f) in Feature.allCases.enumerated() {
            Form.add(CheckRow(f.title, on: settings.isOn(f)) { [unowned self] on in
                settings.set(f, on: on)
                save()
                showFeatureFields()
            }, to: col, top: i == 0 ? 10 : 12)
            Form.add(Form.note(f.details), to: col, top: 4, indent: Form.checkIndent)
        }

        let history = Form.stack()
        Form.add(Form.caption("Settings.SectionHistory"), to: history, top: 28)
        Form.add(themeLabel(Loc.t("Settings.Folder")), to: history, top: 8)
        let folder = NSTextField(string: settings.historyFolder)
        folder.font = Theme.body
        folder.whenClicked { [unowned self, unowned folder] in folderChanged(folder) }
        let browse = ThemeButton(Loc.t("Settings.Browse")) { [unowned self] in browseFolder() }
        let folderLine = NSStackView(views: [folder, browse])
        folderLine.spacing = 8
        Form.add(folderLine, to: history, top: 6)
        folderError = Form.error("")
        Form.add(folderError!, to: history, top: 4).isHidden = true
        folderField = folder

        Form.add(themeLabel(Loc.t("Settings.KeepLast")), to: history, top: 16)
        let limit = NSTextField(string: String(settings.historyLimit))
        limit.font = Theme.mono
        limit.toolTip = Loc.t("Settings.LimitRange")
        limit.widthAnchor.constraint(equalToConstant: 72).isActive = true
        limit.whenClicked { [unowned self, unowned limit] in limitChanged(limit) }
        let limitLine = NSStackView(views: [limit, themeLabel(Loc.t("Settings.Screenshots"), color: Theme.ink70)])
        limitLine.spacing = 10
        Form.add(limitLine, to: history, top: 6, fill: false)
        limitError = Form.error("")
        Form.add(limitError!, to: history, top: 4).isHidden = true
        limitField = limit

        Form.add(CheckRow(Loc.t("Settings.KeepOriginals"), on: settings.keepOriginals) { [unowned self] on in
            let wasOn = settings.keepOriginals
            settings.keepOriginals = on
            save()
            if wasOn && !on { offerDeleteOriginals() }
        }, to: history, top: 16)
        Form.add(Form.note(Loc.t("Settings.KeepOriginalsNote")), to: history, top: 4, indent: Form.checkIndent)
        historySection = Form.add(history, to: col, top: 0)

        Form.add(Form.caption("Settings.SectionBehavior"), to: col, top: 28)
        let behavior = Form.stack()
        enterCopiesRow = Form.add(CheckRow(Loc.t("Settings.EnterCopies"), on: settings.copyOnCapture) { [unowned self] on in
            settings.copyOnCapture = on; save()
        }, to: behavior, top: 0)
        Form.add(CheckRow(Loc.t("Mac.Settings.OpenAtLogin"), on: LoginItem.isOn) { [unowned self] on in setLogin(on) }, to: behavior, top: 12)
        loginError = Form.error("")
        Form.add(loginError!, to: behavior, top: 4, indent: Form.checkIndent).isHidden = true
        excludePinsRow = Form.add(CheckRow(Loc.t("Settings.ExcludePins"), on: settings.excludePinsFromCapture) { [unowned self] on in
            settings.excludePinsFromCapture = on; save()
        }, to: behavior, top: 12)
        Form.add(CheckRow(Loc.t("Settings.Snap"), on: settings.snapEnabled) { [unowned self] on in
            settings.snapEnabled = on; save()
        }, to: behavior, top: 12)
        Form.add(Form.note(Loc.t("Settings.SnapNote")), to: behavior, top: 4, indent: Form.checkIndent)
        Form.add(CheckRow(Loc.t("Common.CheckUpdatesBox"), on: settings.checkForUpdates) { [unowned self] on in
            settings.checkForUpdates = on; save()
        }, to: behavior, top: 12)
        Form.add(Form.note(Loc.t("Mac.Settings.PrivacyNote")), to: behavior, top: 4, indent: Form.checkIndent)
        Form.add(behavior, to: col, top: 10)

        // About: version, manual update check (same as the menu item, works without the box above) and links.
        Form.add(Form.caption("Settings.SectionAbout"), to: col, top: 28)
        Form.add(themeLabel("Screeny \(Self.version)"), to: col, top: 10)
        Form.add(ThemeButton(Loc.t("About.CheckNow")) { [unowned self] in services.checkForUpdates(interactive: true) }, to: col, top: 12, fill: false)
        let repo = "https://github.com/DontIgnore/Screeny"
        let links = NSStackView(views: [
            Form.link(Loc.t("Update.WhatsNew")) { Self.open("\(repo)/releases/tag/v\(Self.version)") },
            Form.link(Loc.t("About.License")) { Self.open("\(repo)/blob/main/LICENSE") },
            Form.link("GitHub") { Self.open(repo) },
        ])
        links.spacing = 20
        Form.add(links, to: col, top: 14, fill: false)
        let feedback = NSStackView(views: [
            Form.link(Loc.t("Tray.ReportBug")) { [unowned self] in services.showFeedback(bug: true) },
            Form.link(Loc.t("Tray.SuggestIdea")) { [unowned self] in services.showFeedback(bug: false) },
        ])
        feedback.spacing = 20
        Form.add(feedback, to: col, top: 8, fill: false)

        scroll.setContent(col)
        showFeatureFields()
    }

    static var version: String { Bundle.main.infoDictionary?["CFBundleShortVersionString"] as? String ?? "dev" }

    private static func open(_ url: String) {
        if let u = URL(string: url) { NSWorkspace.shared.open(u) }
    }

    /// A turned-off feature hides its fields right away (history: its section and "Enter copies"; pins: its box).
    private func showFeatureFields() {
        historySection?.isHidden = !settings.isOn(.history)
        enterCopiesRow?.isHidden = !settings.isOn(.history)
        excludePinsRow?.isHidden = !settings.isOn(.pins)
    }

    private func folderChanged(_ field: NSTextField) {
        let path = field.stringValue.trimmingCharacters(in: .whitespaces)
        let bad = !path.hasPrefix("/")
        folderError?.stringValue = Loc.t("Settings.FolderRequired")
        folderError?.superview?.isHidden = !bad
        guard !bad, path != settings.historyFolder else { return }
        settings.historyFolder = path
        save()
    }

    private func limitChanged(_ field: NSTextField) {
        let n = Int(field.stringValue.trimmingCharacters(in: .whitespaces))
        let ok = n.map { (AppSettings.minHistoryLimit...AppSettings.maxHistoryLimit).contains($0) } ?? false
        limitError?.stringValue = Loc.f("Settings.LimitInvalid", AppSettings.minHistoryLimit, AppSettings.maxHistoryLimit)
        limitError?.superview?.isHidden = ok
        guard ok, let n, n != settings.historyLimit else { return }
        settings.historyLimit = n
        save()
    }

    private func browseFolder() {
        let panel = NSOpenPanel()
        panel.canChooseDirectories = true
        panel.canChooseFiles = false
        panel.canCreateDirectories = true
        panel.message = Loc.t("Settings.HistoryFolderDialog")
        if let p = folderField?.stringValue, FileManager.default.fileExists(atPath: p) { panel.directoryURL = URL(fileURLWithPath: p) }
        guard let w = window else { return }
        panel.beginSheetModal(for: w) { [weak self] r in
            guard r == .OK, let url = panel.url, let self, let field = folderField else { return }
            field.stringValue = url.path
            folderChanged(field)
        }
    }

    private func setLogin(_ on: Bool) {
        let error = LoginItem.set(on)
        loginError?.stringValue = error ?? ""
        loginError?.superview?.isHidden = error == nil
        settings.startAtLogin = LoginItem.isOn
        save()
    }

    /// Keep originals turned off: offer to delete the ones already saved (App.OfferDeleteOriginals).
    private func offerDeleteOriginals() {
        let count = services.history.listOriginals().count
        guard count > 0, let w = window else { return }
        let a = NSAlert()
        a.messageText = Loc.f("App.DeleteOriginalsTitle", count)
        a.informativeText = Loc.t("App.DeleteOriginalsText")
        a.addButton(withTitle: Loc.t("Common.Delete")).hasDestructiveAction = true
        a.addButton(withTitle: Loc.t("App.Keep"))
        a.beginSheetModal(for: w) { [weak self] r in
            guard r == .alertFirstButtonReturn, let self else { return }
            let (deleted, total) = services.history.deleteOriginals()
            guard deleted < total, let w = window else { return }
            let info = NSAlert()
            info.messageText = Loc.t("App.OriginalsPartlyDeletedTitle")
            info.informativeText = Loc.f("App.OriginalsPartlyDeletedText", deleted, total)
            info.beginSheetModal(for: w)
        }
    }
}

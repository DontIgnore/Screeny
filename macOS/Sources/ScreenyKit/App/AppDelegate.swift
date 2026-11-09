import AppKit
import ServiceManagement

/// Menu bar app: global shortcut → frozen overlay on every display → copy / save / pin / history (App.xaml.cs).
final class AppDelegate: NSObject, NSApplicationDelegate, AppServices {
    private(set) var settings = AppSettings.load()
    private var statusItem: NSStatusItem?
    let hotkeys = HotkeyManager()
    private(set) var session: CaptureSession?
    private var historyWindow: HistoryWindowController?
    private var settingsWindow: SettingsWindowController?
    private var wizardWindow: SetupWizardWindowController?
    private var feedbackWindow: FeedbackWindowController?
    private var editors: [EditorWindowController] = []
    private var failedHotkeys: [HotkeySpec] = []
    let historyChanged = Notification.Name("ScreenyHistoryChanged")
    private let saveQueue = DispatchQueue(label: "screeny.save", qos: .userInitiated)
    private var capturing = false

    var history: HistoryStore { HistoryStore(folder: URL(fileURLWithPath: settings.historyFolder, isDirectory: true), limit: settings.historyLimit) }

    func applicationDidFinishLaunching(_ n: Notification) {
        Loc.setLanguage(settings.language)
        if settings.installId == nil { settings.installId = UUID().uuidString; try? settings.save() }
        buildStatusItem()
        installMainMenu()
        hotkeys.pressed = { [weak self] in self?.startCapture() }
        applyHotkeys()
        NotificationCenter.default.addObserver(forName: Loc.changed, object: nil, queue: .main) { [weak self] _ in
            self?.rebuildMenu(); self?.installMainMenu()
        }
        NotificationCenter.default.addObserver(forName: HotkeyRecorder.endedRecording, object: nil, queue: .main) { [weak self] _ in
            self?.applyHotkeys()
        }
        if DevScenarios.handle(CommandLine.arguments, app: self) { return }
        if settings.setupWizardVersion < 1 { showSetupWizard() }
        startUpdateChecks()
        selfTestCapture()
    }

    /// Once Screen Recording is granted, quietly capture every display once and confirm the pixels are real
    /// (not the blank image macOS returns without permission). The result goes to the log and a short notice.
    private func selfTestCapture() {
        guard ScreenCapture.hasPermission else { Log.error("Self-test: no Screen Recording permission yet"); return }
        ScreenCapture.captureAll(excludingWindows: []) { [weak self] r in
            switch r {
            case .success(let shots):
                let ok = shots.allSatisfy { s in
                    let img = s.image
                    var seen = Set<UInt32>()
                    for i in stride(from: 0, to: img.width * img.height, by: max(1, img.width * img.height / 4000)) {
                        let o = i * 4
                        seen.insert(UInt32(img.pixels[o]) << 16 | UInt32(img.pixels[o + 1]) << 8 | UInt32(img.pixels[o + 2]))
                        if seen.count > 8 { return true }
                    }
                    return false
                }
                let sizes = shots.map { "\($0.image.width)×\($0.image.height)" }.joined(separator: ", ")
                Log.error("Self-test: capture \(ok ? "OK" : "returned a blank picture") \(sizes)")
                if ok, let t = self?.hotkeyText { Toast.show("Screeny ✓ \(t)") }
            case .failure(let e):
                Log.error("Self-test: capture failed: \(e)")
            }
        }
    }

    /// Opening Screeny again from Finder or Spotlight (it's already running) takes a screenshot, like on Windows.
    func applicationShouldHandleReopen(_ sender: NSApplication, hasVisibleWindows flag: Bool) -> Bool {
        startCapture()
        return false
    }

    func applicationShouldTerminateAfterLastWindowClosed(_ sender: NSApplication) -> Bool { false }

    // MARK: Menu bar

    private func buildStatusItem() {
        let item = NSStatusBar.system.statusItem(withLength: NSStatusItem.squareLength)
        item.button?.image = StatusIcon.image()
        item.button?.target = self
        item.button?.action = #selector(statusClicked)
        item.button?.sendAction(on: [.leftMouseUp, .rightMouseUp])
        statusItem = item
        rebuildMenu()
    }

    private lazy var menu = NSMenu()

    private func rebuildMenu() {
        menu.removeAllItems()
        func add(_ key: String, _ sel: Selector, keyEquivalent: String = "") {
            let i = NSMenuItem(title: Loc.t(key), action: sel, keyEquivalent: keyEquivalent)
            i.target = self
            menu.addItem(i)
        }
        add("Tray.Capture", #selector(menuCapture))
        if let s = settings.hotkeys.first(where: { $0.enabled }) {
            menu.items.last?.keyEquivalentModifierMask = s.flags.intersection([.command, .shift, .option, .control])
            menu.items.last?.keyEquivalent = HotkeySpec.keyName(s.keyCode).lowercased().count == 1 ? HotkeySpec.keyName(s.keyCode).lowercased() : ""
        }
        if settings.isOn(.history) {
            menu.addItem(.separator())
            add("Tray.History", #selector(menuHistory))
            add("Tray.OpenHistoryFolder", #selector(menuOpenFolder))
        }
        menu.addItem(.separator())
        add("Tray.Settings", #selector(menuSettings), keyEquivalent: ",")
        add("Mac.Tray.Setup", #selector(menuWizard))
        add("Tray.CheckUpdates", #selector(menuUpdates))
        menu.addItem(.separator())
        add("Tray.ReportBug", #selector(menuBug))
        add("Tray.SuggestIdea", #selector(menuIdea))
        menu.addItem(.separator())
        add("Tray.Exit", #selector(menuQuit), keyEquivalent: "q")
        statusItem?.button?.toolTip = tooltip
    }

    private var tooltip: String {
        let v = Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String ?? "dev"
        if let t = hotkeyText { return Loc.f("Tray.TipHotkeys", v, t) }
        return Loc.f("Tray.TipClick", v)
    }

    @objc private func statusClicked() {
        let e = NSApp.currentEvent
        if e?.type == .rightMouseUp || e?.modifierFlags.contains(.control) == true || e?.modifierFlags.contains(.option) == true {
            statusItem?.menu = menu
            statusItem?.button?.performClick(nil)
            statusItem?.menu = nil
        } else {
            startCapture()
        }
    }

    @objc private func menuCapture() { DispatchQueue.main.asyncAfter(deadline: .now() + 0.25) { self.startCapture() } }
    @objc private func menuHistory() { showHistory() }
    @objc private func menuOpenFolder() {
        let url = URL(fileURLWithPath: settings.historyFolder, isDirectory: true)
        do { try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true); NSWorkspace.shared.open(url) }
        catch { toast(Loc.f("App.OpenHistoryFolderFailed", error.localizedDescription)) }
    }
    @objc private func menuSettings() { showSettings() }
    @objc private func menuWizard() { showSetupWizard() }
    @objc private func menuUpdates() { checkForUpdates(interactive: true) }
    @objc private func menuBug() { showFeedback(bug: true) }
    @objc private func menuIdea() { showFeedback(bug: false) }
    @objc private func menuQuit() { NSApp.terminate(nil) }

    /// Standard Edit menu so ⌘C/⌘V/⌘A work in text fields of our windows.
    private func installMainMenu() {
        let main = NSMenu()
        let appItem = NSMenuItem(); main.addItem(appItem)
        let appMenu = NSMenu()
        appMenu.addItem(withTitle: Loc.t("Tray.Settings"), action: #selector(menuSettings), keyEquivalent: ",").target = self
        appMenu.addItem(.separator())
        appMenu.addItem(withTitle: Loc.t("Tray.Exit"), action: #selector(NSApplication.terminate(_:)), keyEquivalent: "q")
        appItem.submenu = appMenu
        let editItem = NSMenuItem(); main.addItem(editItem)
        let edit = NSMenu(title: "Edit")
        edit.addItem(withTitle: "Undo", action: Selector(("undo:")), keyEquivalent: "z")
        let redo = edit.addItem(withTitle: "Redo", action: Selector(("redo:")), keyEquivalent: "z")
        redo.keyEquivalentModifierMask = [.command, .shift]
        edit.addItem(.separator())
        edit.addItem(withTitle: "Cut", action: #selector(NSText.cut(_:)), keyEquivalent: "x")
        edit.addItem(withTitle: "Copy", action: #selector(NSText.copy(_:)), keyEquivalent: "c")
        edit.addItem(withTitle: "Paste", action: #selector(NSText.paste(_:)), keyEquivalent: "v")
        edit.addItem(withTitle: "Select All", action: #selector(NSText.selectAll(_:)), keyEquivalent: "a")
        editItem.submenu = edit
        let windowItem = NSMenuItem(); main.addItem(windowItem)
        let win = NSMenu(title: "Window")
        win.addItem(withTitle: Loc.t("Common.Close"), action: #selector(NSWindow.performClose(_:)), keyEquivalent: "w")
        win.addItem(withTitle: Loc.t("Common.Minimize"), action: #selector(NSWindow.performMiniaturize(_:)), keyEquivalent: "m")
        windowItem.submenu = win
        NSApp.mainMenu = main
    }

    // MARK: Hotkeys

    private func applyHotkeys() {
        failedHotkeys = hotkeys.register(settings.hotkeys)
        for f in failedHotkeys { Log.error("Hotkey \(f.displayText) is taken") }
        if !failedHotkeys.isEmpty && failedHotkeys.count == settings.hotkeys.filter(\.enabled).count {
            toast(Loc.f("Hotkeys.TrayOnly", Loc.f("Hotkeys.Busy", failedHotkeys.map(\.displayText).joined(separator: ", "))))
        }
        statusItem?.button?.toolTip = tooltip
    }

    var hotkeyText: String? {
        let ok = settings.hotkeys.filter { $0.enabled && !failedHotkeys.contains($0) }
        return ok.isEmpty ? nil : ok.map(\.displayText).joined(separator: ", ")
    }

    // MARK: Capture

    func startCapture() {
        if let s = session { s.focus(); return }
        if capturing { return }
        capturing = true
        let previous = NSWorkspace.shared.frontmostApplication
        let exclude = settings.excludePinsFromCapture ? PinWindow.windowNumbers.map { CGWindowID($0) } : []
        // Without Screen Recording permission macOS's screen picker asks which display to capture (no permission needed).
        let capture: (@escaping (Result<[DisplayShot], CaptureError>) -> Void) -> Void = ScreenCapture.hasPermission
            ? { ScreenCapture.captureAll(excludingWindows: exclude, completion: $0) }
            : { ScreenCapture.captureViaPicker(completion: $0) }
        capture { [weak self] result in
            guard let self else { return }
            self.capturing = false
            switch result {
            case .failure(.cancelled):
                break
            case .failure(.noPermission):
                self.showSetupWizard()
            case .failure(let e):
                Log.error("Capture failed: \(e)")
                self.toast(Loc.t("App.CaptureFailed"))
            case .success(let shots):
                let s = CaptureSession(shots: shots, settings: self.settings, previousApp: previous)
                s.finished = { [weak self] r in
                    self?.session = nil
                    self?.persistEditorChoices()
                    if let r { self?.completeCapture(r) }
                }
                self.session = s
                s.show()
            }
        }
    }

    /// Last color, thickness, options and frame choices are kept for the next screenshot.
    private func persistEditorChoices() { try? settings.save() }

    private func completeCapture(_ r: CaptureResult) {
        if r.action == .copyText {
            let text = r.text ?? ""
            NSPasteboard.general.clearContents()
            if NSPasteboard.general.setString(text, forType: .string) {
                let preview = text.replacingOccurrences(of: "\n", with: " ")
                toast(Loc.f("App.TextCopied", preview.count > 80 ? String(preview.prefix(80)) + "…" : preview))
            } else { toast(Loc.t("App.ClipboardBusyText")) }
            return
        }
        let copy = r.action == .copyExplicit || (r.action == .copy && (settings.copyOnCapture || !settings.isOn(.history)))
        if copy && !copyImage(r.image, scale: r.scale) { toast(Loc.t("App.ClipboardBusyNotCopied")) }
        if r.action == .pin { pin(r.image, scale: r.scale, near: CGPoint(x: r.screenRect.minX, y: r.screenRect.maxY)) }
        saveQueue.async { [self] in
            var inHistory = false
            do {
                guard let png = PixelImage.encode(r.image, type: .png) else { throw CocoaError(.fileWriteUnknown) }
                if settings.isOn(.history) {
                    do {
                        let orig = r.original.flatMap { PixelImage.encode($0, type: .png) }
                        try history.save(png: png, json: r.annotationsJSON, originalPng: orig)
                        inHistory = true
                        DispatchQueue.main.async { NotificationCenter.default.post(name: self.historyChanged, object: nil) }
                    } catch {
                        Log.error("History save failed", error)
                        DispatchQueue.main.async { self.toast(Loc.f("App.SaveShotFailed", error.localizedDescription)) }
                    }
                }
                if r.action == .save, let url = r.saveURL {
                    let data = url.pathExtension.lowercased().hasPrefix("jp") ? PixelImage.encode(r.image, type: .jpeg) : png
                    try data?.write(to: url, options: .atomic)
                }
            } catch {
                Log.error("Post-capture action failed", error)
                let msg = Loc.f(r.action == .save ? "App.SaveFailed" : r.action == .pin ? "App.PinFailed" : "App.CopyFailed", error.localizedDescription)
                DispatchQueue.main.async { self.toast(inHistory ? Loc.f("App.FailedSavedToHistory", msg) : msg) }
            }
        }
    }

    @discardableResult
    func copyImage(_ image: CGImage, scale: CGFloat) -> Bool { Clipboard.copy(image, scale: scale) }

    func pin(_ image: CGImage, scale: CGFloat) { pin(image, scale: scale, near: nil) }
    func pin(_ image: CGImage, scale: CGFloat, near: CGPoint?) {
        guard settings.isOn(.pins) else { return }
        PinWindow.open(image: image, scale: scale, services: self, near: near)
    }

    func toast(_ text: String) { Toast.show(text) }

    // MARK: Windows

    private func bringForward(_ w: NSWindow?) {
        NSApp.activate(ignoringOtherApps: true)
        w?.makeKeyAndOrderFront(nil)
    }

    func showHistory() {
        guard settings.isOn(.history) else { return }
        if historyWindow == nil { historyWindow = HistoryWindowController(services: self) }
        historyWindow?.show()
        bringForward(historyWindow?.window)
    }

    func openEditor(_ item: HistoryItem) {
        guard let c = EditorWindowController(item: item, services: self) else {
            let a = NSAlert(); a.messageText = Loc.t("App.OpenShotFailedTitle"); a.informativeText = Loc.t("App.OpenShotFailedText"); a.runModal()
            return
        }
        editors.append(c)
        c.closed = { [weak self, weak c] in self?.editors.removeAll { $0 === c } }
        c.showWindow(nil)
        bringForward(c.window)
    }

    func showSettings() {
        if settingsWindow == nil { settingsWindow = SettingsWindowController(services: self) }
        settingsWindow?.show()
        bringForward(settingsWindow?.window)
    }

    func showSetupWizard() {
        if wizardWindow == nil {
            wizardWindow = SetupWizardWindowController(services: self) { [weak self] in
                DispatchQueue.main.async { self?.wizardWindow = nil }
            }
        }
        wizardWindow?.show()
        bringForward(wizardWindow?.window)
    }

    func showFeedback(bug: Bool) {
        if feedbackWindow == nil {
            let c = FeedbackWindowController(services: self, bug: bug)
            c.onClose = { [weak self] in self?.feedbackWindow = nil }
            feedbackWindow = c
        }
        feedbackWindow?.show()
        bringForward(feedbackWindow?.window)
    }

    @discardableResult
    func saveSettings() -> Bool {
        settings.normalize()
        Loc.setLanguage(settings.language)
        applyHotkeys()
        rebuildMenu()
        applyLoginItem()
        if !settings.isOn(.history) { historyWindow?.close() }
        for e in editors { e.applyFeatures() }
        do { try settings.save(); return true } catch {
            Log.error("Settings save failed", error)
            let a = NSAlert(); a.messageText = Loc.t("App.SettingsSaveFailed"); a.runModal()
            return false
        }
    }

    private func applyLoginItem() {
        let svc = SMAppService.mainApp
        do {
            if settings.startAtLogin && svc.status != .enabled { try svc.register() }
            if !settings.startAtLogin && svc.status == .enabled { try svc.unregister() }
        } catch { Log.error("Login item change failed", error) }
    }
}

/// Copies a picture so it pastes everywhere: PNG plus TIFF, at the right point size for Retina.
enum Clipboard {
    static func copy(_ image: CGImage, scale: CGFloat) -> Bool {
        let rep = NSBitmapImageRep(cgImage: image)
        rep.size = CGSize(width: CGFloat(image.width) / max(1, scale), height: CGFloat(image.height) / max(1, scale))
        guard let png = rep.representation(using: .png, properties: [:]) else { return false }
        let pb = NSPasteboard.general
        pb.clearContents()
        let item = NSPasteboardItem()
        item.setData(png, forType: .png)
        if let tiff = rep.tiffRepresentation { item.setData(tiff, forType: .tiff) }
        return pb.writeObjects([item])
    }
}

/// Menu bar icon: a viewfinder like the Windows tray icon, drawn as a template image.
enum StatusIcon {
    static func image() -> NSImage {
        let img = NSImage(size: CGSize(width: 18, height: 18), flipped: true) { r in
            let p = NSBezierPath()
            p.lineWidth = 1.8
            p.lineCapStyle = .round
            let i: CGFloat = 2.5, l: CGFloat = 5
            for (x, y, dx, dy) in [(i, i, 1.0, 1.0), (r.maxX - i, i, -1.0, 1.0), (i, r.maxY - i, 1.0, -1.0), (r.maxX - i, r.maxY - i, -1.0, -1.0)] {
                p.move(to: CGPoint(x: x, y: y + dy * l)); p.line(to: CGPoint(x: x, y: y)); p.line(to: CGPoint(x: x + dx * l, y: y))
            }
            NSColor.black.setStroke(); p.stroke()
            NSColor.black.setFill()
            NSBezierPath(ovalIn: CGRect(x: r.midX - 2.5, y: r.midY - 2.5, width: 5, height: 5)).fill()
            return true
        }
        img.isTemplate = true
        return img
    }
}

/// Small dark notice at the bottom of the screen (the Windows tray balloon); never steals focus.
final class Toast {
    private static var window: NSPanel?
    private static var timer: Timer?

    static func show(_ text: String, seconds: Double = 3.5) {
        timer?.invalidate()
        window?.orderOut(nil)
        let label = HudLabel()
        label.font = .systemFont(ofSize: 13)
        label.background = RGBA(0x29, 0x25, 0x24, a: 0xF2)
        label.padding = CGSize(width: 14, height: 9)
        label.radius = 10
        label.maxWidth = 520
        label.text = text
        guard let screen = NSScreen.main else { return }
        let size = label.frame.size
        let frame = CGRect(x: screen.visibleFrame.midX - size.width / 2, y: screen.visibleFrame.minY + 60, width: size.width, height: size.height)
        let w = NSPanel(contentRect: frame, styleMask: [.borderless, .nonactivatingPanel], backing: .buffered, defer: false)
        w.isOpaque = false; w.backgroundColor = .clear; w.hasShadow = true
        w.level = .statusBar
        w.ignoresMouseEvents = true
        w.collectionBehavior = [.canJoinAllSpaces, .fullScreenAuxiliary]
        w.contentView = label
        w.orderFrontRegardless()
        window = w
        timer = Timer.scheduledTimer(withTimeInterval: seconds, repeats: false) { _ in window?.orderOut(nil); window = nil }
    }
}

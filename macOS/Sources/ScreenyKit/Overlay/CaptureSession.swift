import AppKit

/// One capture: an overlay per display, one result. Gives focus back to the app that was in front.
final class CaptureSession {
    private(set) var windows: [OverlayWindow] = []
    private(set) var views: [OverlayView] = []
    private let previousApp: NSRunningApplication?
    private var done = false
    private var observers: [NSObjectProtocol] = []
    private let shownAt = Date()
    var finished: ((CaptureResult?) -> Void)?

    init(shots: [DisplayShot], settings: AppSettings, previousApp: NSRunningApplication?) {
        self.previousApp = previousApp
        for s in shots {
            let w = OverlayWindow(screen: s.screen)
            let v = OverlayView(shot: s, settings: settings)
            w.contentView = v
            v.finished = { [weak self] r in self?.finish(r) }
            v.becameActive = { [weak self] active in
                // Selection lives on one display at a time.
                for o in self?.views ?? [] where o !== active { o.resetSelection() }
            }
            windows.append(w); views.append(v)
        }
    }

    func show() {
        NSApp.activate(ignoringOtherApps: true)
        let mouse = NSEvent.mouseLocation
        for w in windows { w.orderFrontRegardless() }
        let main = windows.first { $0.frame.contains(mouse) } ?? windows.first
        main?.makeKeyAndOrderFront(nil)
        for (w, v) in zip(windows, views) { if w === main { v.didShow() } }
        let nc = NotificationCenter.default
        observers.append(nc.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { [weak self] _ in
            Toast.show(Loc.t("App.DisplaysChanged"))
            self?.finish(nil)
        })
        observers.append(nc.addObserver(forName: NSApplication.didResignActiveNotification, object: nil, queue: .main) { [weak self] _ in
            // Another app took over (⌘Tab, a system dialog): a frozen screen without focus only gets in the way.
            guard let self, Date().timeIntervalSince(self.shownAt) > 0.6, !self.views.contains(where: { $0.dialogOpen }) else { return }
            self.finish(nil)
        })
        // Mouse moves over other displays should go to their overlays.
        NSEvent.addLocalMonitorForEvents(matching: [.mouseMoved]) { [weak self] e in
            guard let self, !self.done else { return e }
            if let w = self.windows.first(where: { $0.frame.contains(NSEvent.mouseLocation) }), w !== NSApp.keyWindow, NSEvent.pressedMouseButtons == 0 {
                w.makeKey()
                (w.contentView as? OverlayView)?.didShow()
            }
            return e
        }.map { monitor = $0 }
    }

    private var monitor: Any?

    func focus() {
        NSApp.activate(ignoringOtherApps: true)
        windows.first { $0.frame.contains(NSEvent.mouseLocation) }?.makeKeyAndOrderFront(nil)
    }

    private func finish(_ r: CaptureResult?) {
        if done { return }
        done = true
        HudTooltip.hideNow()
        observers.forEach(NotificationCenter.default.removeObserver)
        if let m = monitor { NSEvent.removeMonitor(m) }
        for w in windows { w.orderOut(nil); w.close() }
        NSCursor.arrow.set()
        if r?.action != .pin, let p = previousApp, !p.isTerminated, p != NSRunningApplication.current { p.activate() }
        let cb = finished
        finished = nil
        DispatchQueue.main.async {
            cb?(r)
            // Keep the views alive until the run loop is done with their events.
            _ = self.views
        }
    }
}

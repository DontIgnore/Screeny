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

    /// The whole capture is one view over the virtual screen of all displays (one selection, one markup, like Windows);
    /// it lives in the window of the display that holds the selection, other displays show panes onto it.
    init(shots: [DisplayShot], settings: AppSettings, previousApp: NSRunningApplication?) {
        self.previousApp = previousApp
        let shot = ScreenCapture.virtualScreen(shots)
        let master = OverlayView(shot: shot, settings: settings)
        master.finished = { [weak self] r in self?.finish(r) }
        master.afterGesture = { [weak self] in self?.rehome() }
        views = [master]
        for d in shot.displays {
            let w = OverlayWindow(frame: d.frame)
            if shot.displays.count > 1 {
                let pane = OverlayPane(master: master, area: d.frame, virtualFrame: shot.frame, image: shot.image.cgImage)
                w.contentView?.addSubview(pane)
                panes.append(pane)
            }
            windows.append(w)
        }
        master.panes = panes
        home(at: windows.firstIndex { $0.frame.contains(NSEvent.mouseLocation) } ?? 0)
    }

    private var panes: [OverlayPane] = []
    var master: OverlayView { views[0] }

    /// Puts the overlay view into display `i`'s window (offset so its virtual-screen coordinates line up).
    private func home(at i: Int) {
        let w = windows[i], u = master.shotInfo.frame
        if master.window === w { return }
        let typing = master.editor.isTextEditing
        if typing { master.editor.commitTextEdit() }
        master.removeFromSuperview()
        master.frame = CGRect(x: u.minX - w.frame.minX, y: u.minY - w.frame.minY, width: u.width, height: u.height)
        w.contentView?.addSubview(master)
        for (j, p) in panes.enumerated() { p.isHidden = j == i }
        if !done && w.isVisible { w.makeKey(); w.makeFirstResponder(master) }
    }

    /// After a gesture or a mouse move: keep the overlay view on the display that holds the selection (or the mouse).
    func rehome() {
        guard !done, windows.count > 1 else { return }
        let d = master.activeDisplay.frame
        if let i = windows.firstIndex(where: { $0.frame == d }) { home(at: i) }
    }

    func show() {
        NSApp.activate(ignoringOtherApps: true)
        for w in windows { w.orderFrontRegardless() }
        let main = master.window ?? windows[0]
        main.makeKeyAndOrderFront(nil)
        master.didShow()
        let nc = NotificationCenter.default
        observers.append(nc.addObserver(forName: NSApplication.didChangeScreenParametersNotification, object: nil, queue: .main) { [weak self] _ in
            Toast.show(Loc.t("App.DisplaysChanged"))
            self?.finish(nil)
        })
        observers.append(nc.addObserver(forName: NSApplication.didResignActiveNotification, object: nil, queue: .main) { [weak self] _ in
            // Another app took over (⌘Tab, a system dialog): a frozen screen without focus only gets in the way.
            guard let self, !self.done, !self.views.contains(where: { $0.dialogOpen }) else { return }
            if Date().timeIntervalSince(self.shownAt) < 0.6 {
                // Right after showing, the closing status menu can take activation for a moment: take it back.
                DispatchQueue.main.async { self.focus() }
                return
            }
            self.finish(nil)
        })
        // Before anything is selected, the overlay (keyboard, magnifier) follows the display under the mouse.
        NSEvent.addLocalMonitorForEvents(matching: [.mouseMoved]) { [weak self] e in
            guard let self, !self.done, NSApp.modalWindow == nil, NSEvent.pressedMouseButtons == 0,
                  !self.master.dialogOpen, !self.master.hasSelection else { return e }
            self.rehome()
            return e
        }.map { monitor = $0 }
    }

    private var monitor: Any?

    func focus() {
        NSApp.activate(ignoringOtherApps: true)
        (master.window ?? windows.first)?.makeKeyAndOrderFront(nil)
    }

    private func finish(_ r: CaptureResult?) {
        if done { return }
        done = true
        HudTooltip.hideNow()
        observers.forEach(NotificationCenter.default.removeObserver)
        if let m = monitor { NSEvent.removeMonitor(m) }
        for w in windows { w.orderOut(nil); w.close() }
        NSCursor.arrow.set()
        if r?.action != .pin, !views.contains(where: { $0.keepPreviousAppInBack }), let p = previousApp, !p.isTerminated, p != NSRunningApplication.current { p.activate() }
        let cb = finished
        finished = nil
        DispatchQueue.main.async {
            cb?(r)
            // Keep the views alive until the run loop is done with their events.
            _ = self.views
        }
    }
}

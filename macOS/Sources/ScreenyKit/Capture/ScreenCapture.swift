import AppKit
import ScreenCaptureKit

/// A display: its area and the area without the menu bar and Dock, in AppKit global points (y up).
struct DisplayArea: Equatable {
    var frame: CGRect
    var visibleFrame: CGRect
}

/// A frozen screen: the picture in pixels, the area it covers and the windows on it (front to back).
/// One display, or the virtual screen of all displays stitched together (like the Windows overlay).
struct DisplayShot {
    /// The covered area in AppKit global points.
    let frame: CGRect
    /// The displays inside `frame`.
    let displays: [DisplayArea]
    let image: PixelImage
    /// Pixels per point.
    var scale: CGFloat { image.scale }
    let windows: [WindowInfo]
    var raw: [ScreenCapture.RawWindow] = []
}

enum CaptureError: Error { case noPermission, cancelled, failed(String) }

enum ScreenCapture {
    static var hasPermission: Bool { CGPreflightScreenCaptureAccess() }

    static func displayID(_ s: NSScreen) -> CGDirectDisplayID {
        (s.deviceDescription[NSDeviceDescriptionKey("NSScreenNumber")] as? NSNumber)?.uint32Value ?? CGMainDisplayID()
    }

    /// Freezes every display at once (menus and tooltips stay put while selecting).
    static func captureAll(excludingWindows exclude: [CGWindowID], completion: @escaping (Result<[DisplayShot], CaptureError>) -> Void) {
        guard hasPermission else { completion(.failure(.noPermission)); return }
        let screens = NSScreen.screens
        let windowList = windowSnapshot()
        Task {
            do {
                let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
                let excluded = content.windows.filter { exclude.contains($0.windowID) }
                var shots: [DisplayShot] = []
                for screen in screens {
                    let id = displayID(screen)
                    guard let display = content.displays.first(where: { $0.displayID == id }) else { continue }
                    shots.append(try await shot(of: screen, filter: SCContentFilter(display: display, excludingWindows: excluded), windowList: windowList))
                }
                let result = shots
                await MainActor.run { result.isEmpty ? completion(.failure(.failed("no displays"))) : completion(.success(result)) }
            } catch {
                Log.error("Capture failed", error)
                await MainActor.run { completion(.failure(hasPermission ? .failed(error.localizedDescription) : .noPermission)) }
            }
        }
    }

    /// Without Screen Recording permission: macOS's own screen picker (no permission needed; the user clicks the display
    /// to capture each time), then one screenshot of that display through the filter the picker hands back.
    static func captureViaPicker(completion: @escaping (Result<[DisplayShot], CaptureError>) -> Void) {
        PickerCapture.shared.start(completion)
    }

    /// The picker's display as an NSScreen (macOS 15.2 names it; older systems: match by size, prefer the mouse's screen).
    static func screen(for filter: SCContentFilter) -> NSScreen? {
        if #available(macOS 15.2, *), let id = filter.includedDisplays.first?.displayID {
            return NSScreen.screens.first { displayID($0) == id }
        }
        let size = filter.contentRect.size
        let fits = NSScreen.screens.filter { $0.frame.size == size }
        return fits.first { $0.frame.contains(NSEvent.mouseLocation) } ?? fits.first ?? NSScreen.main
    }

    static func shot(of screen: NSScreen, filter: SCContentFilter, windowList: [RawWindow]) async throws -> DisplayShot {
        let scale = screen.backingScaleFactor
        let cfg = SCStreamConfiguration()
        cfg.width = Int((screen.frame.width * scale).rounded())
        cfg.height = Int((screen.frame.height * scale).rounded())
        cfg.showsCursor = false
        cfg.captureResolution = .best
        if #available(macOS 15.0, *) { cfg.captureDynamicRange = .SDR }
        cfg.colorSpaceName = CGColorSpace.sRGB
        let cg = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: cfg)
        let img = PixelImage(cgImage: cg, scale: scale)
        return DisplayShot(frame: screen.frame, displays: [DisplayArea(frame: screen.frame, visibleFrame: screen.visibleFrame)], image: img,
                           windows: windows(windowList, in: screen.frame, scale: scale, pixelBounds: img.bounds), raw: windowList)
    }

    struct RawWindow { let bounds: CGRect; let layer: Int }

    /// Visible windows front to back in global top-left points (CGWindowList order).
    static func windowSnapshot() -> [RawWindow] {
        let me = ProcessInfo.processInfo.processIdentifier
        guard let list = CGWindowListCopyWindowInfo([.optionOnScreenOnly, .excludeDesktopElements], kCGNullWindowID) as? [[String: Any]] else { return [] }
        var out: [RawWindow] = []
        for w in list {
            guard let pid = w[kCGWindowOwnerPID as String] as? Int32, pid != me,
                  let layer = w[kCGWindowLayer as String] as? Int,
                  let bd = w[kCGWindowBounds as String] as? NSDictionary,
                  let rect = CGRect(dictionaryRepresentation: bd) else { continue }
            if let alpha = w[kCGWindowAlpha as String] as? Double, alpha <= 0.01 { continue }
            // Normal windows, floating panels and popup menus; not the Dock, menu bar or desktop.
            guard layer == 0 || layer == 3 || layer == 8 || layer == 101 || layer == 25 else { continue }
            if (w[kCGWindowOwnerName as String] as? String) == "Window Server" { continue }
            if rect.width < 10 || rect.height < 10 { continue }
            out.append(RawWindow(bounds: rect, layer: layer))
        }
        return out
    }

    static func windows(_ raw: [RawWindow], in frame: CGRect, scale: CGFloat, pixelBounds: PixelRect) -> [WindowInfo] {
        // Global CG space: origin at the top-left of the primary display, y down, in points.
        let primaryHeight = NSScreen.screens.first?.frame.height ?? frame.height
        let top = primaryHeight - frame.maxY
        let origin = CGPoint(x: frame.minX, y: top)
        return raw.compactMap { w in
            let r = CGRect(x: (w.bounds.minX - origin.x) * scale, y: (w.bounds.minY - origin.y) * scale,
                           width: w.bounds.width * scale, height: w.bounds.height * scale)
            let pr = PixelRect(enclosing: r).clamped(to: pixelBounds)
            return pr.isEmpty ? nil : WindowInfo(rect: pr)
        }
    }

    /// Stitches displays into one virtual screen at the highest scale, black in the gaps (Windows' virtual-screen overlay).
    static func virtualScreen(_ shots: [DisplayShot]) -> DisplayShot {
        if shots.count == 1 { return shots[0] }
        let u = shots.map(\.frame).reduce(CGRect.null) { $0.union($1) }
        let s = shots.map(\.scale).max() ?? 1
        let w = Int((u.width * s).rounded()), h = Int((u.height * s).rounded())
        let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0, space: PixelImage.colorSpace, bitmapInfo: PixelImage.bitmapInfo)!
        ctx.setFillColor(CGColor(gray: 0, alpha: 1)); ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))
        ctx.interpolationQuality = .high
        for sh in shots {
            let f = sh.frame
            ctx.draw(sh.image.cgImage, in: CGRect(x: (f.minX - u.minX) * s, y: (f.minY - u.minY) * s, width: f.width * s, height: f.height * s))
        }
        let img = PixelImage(cgImage: ctx.makeImage()!, scale: s)
        let raw = shots.first?.raw ?? []
        var wins = windows(raw, in: u, scale: s, pixelBounds: img.bounds)
        if raw.isEmpty {
            // No global window list (QA fixtures): bring each display's windows over.
            wins = shots.flatMap { sh in
                sh.windows.map { win -> WindowInfo in
                    let k = s / sh.scale
                    let r = CGRect(x: CGFloat(win.rect.x) * k + (sh.frame.minX - u.minX) * s, y: CGFloat(win.rect.y) * k + (u.maxY - sh.frame.maxY) * s,
                                   width: CGFloat(win.rect.width) * k, height: CGFloat(win.rect.height) * k)
                    return WindowInfo(rect: PixelRect(enclosing: r).clamped(to: img.bounds))
                }
            }
        }
        return DisplayShot(frame: u, displays: shots.flatMap(\.displays), image: img, windows: wins, raw: raw)
    }

    static func openPermissionSettings() {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")!)
    }
}

/// Keeps the picker observer alive for one capture at a time.
private final class PickerCapture: NSObject, SCContentSharingPickerObserver {
    static let shared = PickerCapture()
    private var completion: ((Result<[DisplayShot], CaptureError>) -> Void)?

    func start(_ completion: @escaping (Result<[DisplayShot], CaptureError>) -> Void) {
        guard self.completion == nil else { return }
        self.completion = completion
        let picker = SCContentSharingPicker.shared
        var cfg = SCContentSharingPickerConfiguration()
        cfg.allowedPickerModes = .singleDisplay
        cfg.allowsChangingSelectedContent = false
        picker.defaultConfiguration = cfg
        picker.add(self)
        picker.isActive = true
        picker.present(using: .display)
    }

    private func done(_ r: Result<[DisplayShot], CaptureError>) {
        let picker = SCContentSharingPicker.shared
        picker.remove(self)
        picker.isActive = false
        let cb = completion
        completion = nil
        cb?(r)
    }

    func contentSharingPicker(_ picker: SCContentSharingPicker, didCancelFor stream: SCStream?) {
        DispatchQueue.main.async { self.done(.failure(.cancelled)) }
    }

    func contentSharingPickerStartDidFailWithError(_ error: any Error) {
        Log.error("Screen picker failed", error)
        DispatchQueue.main.async { self.done(.failure(.failed(error.localizedDescription))) }
    }

    func contentSharingPicker(_ picker: SCContentSharingPicker, didUpdateWith filter: SCContentFilter, for stream: SCStream?) {
        DispatchQueue.main.async {
            guard let screen = ScreenCapture.screen(for: filter) else { self.done(.failure(.failed("no display"))); return }
            let list = ScreenCapture.windowSnapshot()
            Task {
                // Let the picker's highlight fade before the picture is taken.
                try? await Task.sleep(nanoseconds: 250_000_000)
                do {
                    let s = try await ScreenCapture.shot(of: screen, filter: filter, windowList: list)
                    await MainActor.run { self.done(.success([s])) }
                } catch {
                    Log.error("Picker capture failed", error)
                    await MainActor.run { self.done(.failure(.failed(error.localizedDescription))) }
                }
            }
        }
    }
}

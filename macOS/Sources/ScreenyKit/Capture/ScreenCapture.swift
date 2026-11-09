import AppKit
import ScreenCaptureKit

/// One frozen display: the picture in pixels, where the display is, and the windows on it (front to back).
struct DisplayShot {
    let screen: NSScreen
    let displayID: CGDirectDisplayID
    let image: PixelImage
    /// Pixels per point.
    var scale: CGFloat { image.scale }
    let windows: [WindowInfo]
}

enum CaptureError: Error { case noPermission, failed(String) }

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
                    let scale = screen.backingScaleFactor
                    let cfg = SCStreamConfiguration()
                    cfg.width = Int((screen.frame.width * scale).rounded())
                    cfg.height = Int((screen.frame.height * scale).rounded())
                    cfg.showsCursor = false
                    cfg.captureResolution = .best
                    if #available(macOS 15.0, *) { cfg.captureDynamicRange = .SDR }
                    cfg.colorSpaceName = CGColorSpace.sRGB
                    let filter = SCContentFilter(display: display, excludingWindows: excluded)
                    let cg = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: cfg)
                    let img = PixelImage(cgImage: cg, scale: scale)
                    shots.append(DisplayShot(screen: screen, displayID: id, image: img,
                                             windows: windows(windowList, on: screen, scale: scale, pixelBounds: img.bounds)))
                }
                let result = shots
                await MainActor.run { result.isEmpty ? completion(.failure(.failed("no displays"))) : completion(.success(result)) }
            } catch {
                Log.error("Capture failed", error)
                await MainActor.run { completion(.failure(hasPermission ? .failed(error.localizedDescription) : .noPermission)) }
            }
        }
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

    static func windows(_ raw: [RawWindow], on screen: NSScreen, scale: CGFloat, pixelBounds: PixelRect) -> [WindowInfo] {
        // Global CG space: origin at the top-left of the primary display, y down, in points.
        let primaryHeight = NSScreen.screens.first?.frame.height ?? screen.frame.height
        let top = primaryHeight - screen.frame.maxY
        let origin = CGPoint(x: screen.frame.minX, y: top)
        return raw.compactMap { w in
            let r = CGRect(x: (w.bounds.minX - origin.x) * scale, y: (w.bounds.minY - origin.y) * scale,
                           width: w.bounds.width * scale, height: w.bounds.height * scale)
            let pr = PixelRect(enclosing: r).clamped(to: pixelBounds)
            return pr.isEmpty ? nil : WindowInfo(rect: pr)
        }
    }

    static func openPermissionSettings() {
        NSWorkspace.shared.open(URL(string: "x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture")!)
    }
}

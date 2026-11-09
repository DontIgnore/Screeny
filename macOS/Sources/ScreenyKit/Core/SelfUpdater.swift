import AppKit
import Security

/// Installs a newer build from a release zip (the macOS counterpart of Velopack's "Update and restart"):
/// download → unpack → check the new app is signed by the same developer as this one → swap the bundle → relaunch.
/// The swap happens after Screeny quits, so a running copy is never overwritten in place.
enum SelfUpdater {
    enum Failure: LocalizedError {
        case noApp, signature(String), notInstalled
        var errorDescription: String? {
            switch self {
            case .noApp: return "the download has no Screeny.app inside"
            case .signature(let m): return "the new version isn't signed by the same developer (\(m))"
            case .notInstalled: return "this copy isn't running from an app bundle"
            }
        }
    }

    private static var task: URLSessionDownloadTask?
    private static var observation: NSKeyValueObservation?

    static func install(from url: URL, progress: @escaping (Double) -> Void, completion: @escaping (Error?) -> Void) {
        let current = Bundle.main.bundleURL
        guard current.pathExtension == "app" else { completion(Failure.notInstalled); return }
        let t = URLSession.shared.downloadTask(with: url) { file, _, error in
            let result: Error? = {
                if let error { return error }
                guard let file else { return URLError(.cannotOpenFile) }
                do {
                    let app = try unpack(file)
                    try verify(app)
                    try scheduleSwap(new: app, replacing: current)
                    return nil
                } catch { return error }
            }()
            DispatchQueue.main.async {
                completion(result)
                if result == nil { NSApp.terminate(nil) }
            }
        }
        observation = t.progress.observe(\.fractionCompleted) { p, _ in DispatchQueue.main.async { progress(p.fractionCompleted) } }
        task = t
        t.resume()
    }

    /// Unzips into a fresh temporary folder and returns the app inside.
    static func unpack(_ zip: URL) throws -> URL {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("screeny-update-\(UUID().uuidString)")
        try FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/usr/bin/ditto")
        p.arguments = ["-x", "-k", zip.path, dir.path]
        try p.run(); p.waitUntilExit()
        guard p.terminationStatus == 0 else { throw CocoaError(.fileReadCorruptFile) }
        let items = (try? FileManager.default.contentsOfDirectory(at: dir, includingPropertiesForKeys: nil)) ?? []
        guard let app = items.first(where: { $0.pathExtension == "app" }) else { throw Failure.noApp }
        return app
    }

    /// The new bundle must be intact and satisfy this app's own designated requirement (same bundle id and certificate).
    static func verify(_ app: URL, requirementOf reference: URL = Bundle.main.bundleURL) throws {
        var mine: SecStaticCode?, theirs: SecStaticCode?, req: SecRequirement?
        guard SecStaticCodeCreateWithPath(reference as CFURL, [], &mine) == errSecSuccess, let mine,
              SecCodeCopyDesignatedRequirement(mine, [], &req) == errSecSuccess, let req else { throw Failure.signature("no requirement") }
        guard SecStaticCodeCreateWithPath(app as CFURL, [], &theirs) == errSecSuccess, let theirs else { throw Failure.signature("unreadable") }
        let status = SecStaticCodeCheckValidity(theirs, SecCSFlags(rawValue: kSecCSCheckAllArchitectures | kSecCSStrictValidate), req)
        guard status == errSecSuccess else { throw Failure.signature("OSStatus \(status)") }
    }

    /// A tiny shell script waits for this process to exit, swaps the bundles (keeping the old one until the new one is in place)
    /// and opens the new version.
    static func scheduleSwap(new: URL, replacing current: URL) throws {
        let pid = ProcessInfo.processInfo.processIdentifier
        func q(_ s: String) -> String { "'" + s.replacingOccurrences(of: "'", with: "'\\''") + "'" }
        let backup = current.deletingLastPathComponent().appendingPathComponent(".Screeny-previous.app").path
        let script = """
        while kill -0 \(pid) 2>/dev/null; do sleep 0.2; done
        rm -rf \(q(backup))
        mv \(q(current.path)) \(q(backup)) && mv \(q(new.path)) \(q(current.path)) && rm -rf \(q(backup)) || { [ -d \(q(backup)) ] && [ ! -d \(q(current.path)) ] && mv \(q(backup)) \(q(current.path)); }
        xattr -dr com.apple.quarantine \(q(current.path)) 2>/dev/null
        open \(q(current.path))
        """
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", script]
        try p.run()
    }
}

import Foundation

public struct HistoryItem: Equatable {
    public let url: URL
    public let createdAt: Date
    public var jsonURL: URL?
    public var originalURL: URL?
}

/// Screenshot history folder: `yyyyMMdd_HHmmss_fff[_n].png` + `.screeny.json` + optional `.orig.png`
/// (History/HistoryStore.cs). The same folder layout works with a history copied from Windows.
public final class HistoryStore {
    public static let jsonSuffix = ".screeny.json", originalSuffix = ".orig.png"
    public let folder: URL
    public let limit: Int

    public init(folder: URL, limit: Int) { self.folder = folder; self.limit = max(1, limit) }

    static let stampFormatter: DateFormatter = {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.calendar = Calendar(identifier: .gregorian)
        f.timeZone = .current
        f.dateFormat = "yyyyMMdd_HHmmss_SSS"
        f.isLenient = false
        return f
    }()

    private static let namePattern = try! NSRegularExpression(pattern: "^(\\d{8}_\\d{6}_\\d{3})(?:_([1-9]\\d{0,8}))?\\.png$", options: .caseInsensitive)
    private static let sidecarPattern = try! NSRegularExpression(pattern: "^(\\d{8}_\\d{6}_\\d{3}(?:_[1-9]\\d{0,8})?)\\.(?:screeny\\.json|orig\\.png)$", options: .caseInsensitive)

    public static func parseName(_ name: String) -> (Date, Int)? {
        let ns = name as NSString
        guard let m = namePattern.firstMatch(in: name, range: NSRange(location: 0, length: ns.length)) else { return nil }
        let stamp = ns.substring(with: m.range(at: 1))
        guard let date = stampFormatter.date(from: stamp), stampFormatter.string(from: date) == stamp else { return nil }
        let n = m.range(at: 2).location == NSNotFound ? 0 : Int(ns.substring(with: m.range(at: 2))) ?? 0
        return (date, n)
    }

    public static func sidecars(_ png: URL) -> (json: URL, original: URL) {
        let stem = png.deletingPathExtension().lastPathComponent
        let dir = png.deletingLastPathComponent()
        return (dir.appendingPathComponent(stem + jsonSuffix), dir.appendingPathComponent(stem + originalSuffix))
    }

    private var fm: FileManager { .default }
    /// Saves come from the capture queue and the editor at the same time. Without this, one save's orphan cleanup can
    /// delete the other's sidecar before its picture lands, and saves in the same millisecond share temp files.
    /// Static: settings can create a new store for the same folder. Recursive: save → prune → delete.
    private static let lock = NSRecursiveLock()

    /// Writes sidecars first and the picture last, each through a temp file; on failure nothing is left behind.
    @discardableResult
    public func save(png: Data, json: String? = nil, originalPng: Data? = nil, at time: Date = Date()) throws -> URL {
        Self.lock.lock(); defer { Self.lock.unlock() }
        try fm.createDirectory(at: folder, withIntermediateDirectories: true)
        let url = nextFreeURL(Self.stampFormatter.string(from: time))
        let (jsonURL, origURL) = Self.sidecars(url)
        var files: [(URL, Data)] = []
        if let json { files.append((jsonURL, Data(json.utf8))) }
        if let originalPng { files.append((origURL, originalPng)) }
        files.append((url, png))
        var temps: [URL] = [], moved: [URL] = []
        do {
            for (final, data) in files {
                let tmp = final.appendingPathExtension("tmp")
                guard fm.createFile(atPath: tmp.path, contents: data) else { throw CocoaError(.fileWriteUnknown) }
                temps.append(tmp)
            }
            for (final, _) in files {
                try fm.moveItem(at: final.appendingPathExtension("tmp"), to: final)
                moved.append(final)
            }
        } catch {
            for t in temps { try? fm.removeItem(at: t) }
            for m in moved { try? fm.removeItem(at: m) }
            throw error
        }
        prune(keep: url)
        return url
    }

    private func nextFreeURL(_ stamp: String) -> URL {
        var i = 0
        while true {
            let url = folder.appendingPathComponent(i == 0 ? "\(stamp).png" : "\(stamp)_\(i).png")
            let (j, o) = Self.sidecars(url)
            // An orphaned sidecar with this name must not stick to a new screenshot.
            if ![url, j, o].contains(where: { fm.fileExists(atPath: $0.path) }) { return url }
            i += 1
        }
    }

    public func list() -> [HistoryItem] {
        guard let names = try? fm.contentsOfDirectory(atPath: folder.path) else { return [] }
        let lower = Set(names.map { $0.lowercased() })
        var shots: [(URL, Date, Int)] = []
        for n in names { if let (d, s) = Self.parseName(n) { shots.append((folder.appendingPathComponent(n), d, s)) } }
        return shots.sorted { ($0.1, $0.2) > ($1.1, $1.2) }.map { s in
            let (j, o) = Self.sidecars(s.0)
            return HistoryItem(url: s.0, createdAt: s.1,
                               jsonURL: lower.contains(j.lastPathComponent.lowercased()) ? j : nil,
                               originalURL: lower.contains(o.lastPathComponent.lowercased()) ? o : nil)
        }
    }

    public func delete(_ url: URL) throws {
        Self.lock.lock(); defer { Self.lock.unlock() }
        if fm.fileExists(atPath: url.path) { try fm.removeItem(at: url) }
        guard Self.parseName(url.lastPathComponent) != nil else { return }
        let (j, o) = Self.sidecars(url)
        for s in [j, o] where fm.fileExists(atPath: s.path) {
            do { try fm.removeItem(at: s) } catch { Log.error("History sidecar delete failed: \(s.path)", error) }
        }
    }

    public func clear() {
        Self.lock.lock(); defer { Self.lock.unlock() }
        for i in list() { try? delete(i.url) }
        deleteOrphanSidecars()
    }

    public func listOriginals() -> [URL] {
        guard let names = try? fm.contentsOfDirectory(atPath: folder.path) else { return [] }
        return names.filter { n in
            n.lowercased().hasSuffix(Self.originalSuffix) && Self.parseName(String(n.dropLast(Self.originalSuffix.count)) + ".png") != nil
        }.map { folder.appendingPathComponent($0) }
    }

    @discardableResult public func deleteOriginals() -> (deleted: Int, total: Int) {
        Self.lock.lock(); defer { Self.lock.unlock() }
        let all = listOriginals()
        var n = 0
        for u in all { do { try fm.removeItem(at: u); n += 1 } catch { Log.error("History original delete failed: \(u.path)", error) } }
        return (n, all.count)
    }

    private func prune(keep: URL) {
        for item in list().filter({ $0.url != keep }).dropFirst(limit - 1) {
            do { try delete(item.url) } catch { Log.error("History prune failed: \(item.url.path)", error) }
        }
        deleteOrphanSidecars()
    }

    private func deleteOrphanSidecars() {
        guard let names = try? fm.contentsOfDirectory(atPath: folder.path) else { return }
        for n in names {
            let ns = n as NSString
            guard let m = Self.sidecarPattern.firstMatch(in: n, range: NSRange(location: 0, length: ns.length)) else { continue }
            let shot = ns.substring(with: m.range(at: 1)) + ".png"
            if Self.parseName(shot) == nil || fm.fileExists(atPath: folder.appendingPathComponent(shot).path) { continue }
            try? fm.removeItem(at: folder.appendingPathComponent(n))
        }
    }
}

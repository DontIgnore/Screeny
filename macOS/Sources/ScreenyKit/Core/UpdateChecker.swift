import AppKit

/// The latest GitHub release.
public struct ReleaseInfo: Equatable {
    /// Without the leading "v" of the tag.
    public var version: String
    public var notes: String?
    public var pageURL: URL
    /// The macOS build attached to the release (a zip with Screeny.app), if there is one.
    public var macAssetURL: URL? = nil
}

public enum UpdateCheckResult: Equatable {
    case available(ReleaseInfo)
    /// Newer, but the user skipped this version (automatic checks only).
    case skipped(ReleaseInfo)
    case upToDate(current: String)
    /// Text ready to show: a friendly About.Reason* text or the raw error.
    case failed(String)
}

/// Update check against GitHub Releases. The Mac app can't install updates itself (Windows uses Velopack): a newer
/// version opens the update window, whose main button opens the release page in the browser.
public enum UpdateChecker {
    public static let latestURL = URL(string: "https://api.github.com/repos/DontIgnore/Screeny/releases/latest")!
    public static let startupDelay: TimeInterval = 30
    public static let interval: TimeInterval = 24 * 60 * 60
    /// Release-notes tag of the downloadable precise-OCR module: Windows only, so never "on" here.
    static let preciseOcrTag = "ocr-precise"

    private static var timer: Timer?
    private static var window: UpdateWindowController?

    // MARK: Network

    /// Completion on the main queue. Interactive checks ignore a skipped version (like the Windows manual check).
    public static func check(settings: AppSettings, interactive: Bool, completion: @escaping (UpdateCheckResult) -> Void) {
        var req = URLRequest(url: latestURL, timeoutInterval: 30)
        req.setValue("application/vnd.github+json", forHTTPHeaderField: "Accept")
        req.setValue("Screeny/\(AppInfo.version)", forHTTPHeaderField: "User-Agent")
        URLSession.shared.dataTask(with: req) { data, response, error in
            let status = (response as? HTTPURLResponse)?.statusCode
            let result: UpdateCheckResult
            if let status, (200..<300).contains(status), let data, let release = parseRelease(data) {
                let current = AppInfo.version
                if compareVersions(release.version, current) != .orderedDescending { result = .upToDate(current: current) }
                else if !interactive, let skipped = settings.skippedUpdateVersion, skipped.caseInsensitiveCompare(release.version) == .orderedSame {
                    result = .skipped(release)
                } else { result = .available(release) }
            } else {
                let key = reasonKey(error: error, status: status)
                let raw = error?.localizedDescription
                    ?? status.map { "HTTP \($0) \(HTTPURLResponse.localizedString(forStatusCode: $0))" } ?? "Unreadable GitHub response"
                Log.error("Update check failed: \(raw)")
                result = .failed(key.map(Loc.t) ?? raw)
            }
            DispatchQueue.main.async { completion(result) }
        }.resume()
    }

    static func parseRelease(_ data: Data) -> ReleaseInfo? {
        guard let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let tag = obj["tag_name"] as? String, !tag.isEmpty else { return nil }
        let page = (obj["html_url"] as? String).flatMap(URL.init(string:)) ?? URL(string: "\(AppInfo.repositoryURL)/releases/tag/\(tag)")!
        let assets = obj["assets"] as? [[String: Any]] ?? []
        let mac = assets.first { a in
            let n = (a["name"] as? String ?? "").lowercased()
            return n.hasSuffix(".zip") && (n.contains("mac") || n.contains("osx") || n.contains("darwin"))
        }.flatMap { ($0["browser_download_url"] as? String).flatMap(URL.init(string:)) }
        return ReleaseInfo(version: normalize(tag), notes: obj["body"] as? String, pageURL: page, macAssetURL: mac)
    }

    /// Friendly reason like Windows (AboutInfo.cs ReasonKeyFor); nil = show the raw error.
    public static func reasonKey(error: Error?, status: Int?) -> String? {
        if let e = error as? URLError {
            switch e.code {
            case .timedOut: return "About.ReasonTimeout"
            case .notConnectedToInternet, .networkConnectionLost, .cannotFindHost, .cannotConnectToHost, .dnsLookupFailed,
                 .internationalRoamingOff, .dataNotAllowed, .secureConnectionFailed: return "About.ReasonNetwork"
            default: return nil
            }
        }
        guard let status else { return nil }
        if status == 403 || status == 429 { return "About.ReasonLimit" }
        if status >= 500 { return "About.ReasonServer" }
        return nil
    }

    // MARK: Versions

    /// "v1.2.3+abc" → "1.2.3".
    public static func normalize(_ v: String) -> String {
        var s = v.trimmingCharacters(in: .whitespaces)
        if s.first == "v" || s.first == "V" { s.removeFirst() }
        return String(s.split(separator: "+", maxSplits: 1, omittingEmptySubsequences: false).first ?? "")
    }

    /// Semantic version order: numeric core (missing parts are 0), a pre-release is lower than its release, pre-release
    /// identifiers compare numerically when both are numbers, numbers sort before words, a longer list wins a tie.
    public static func compareVersions(_ a: String, _ b: String) -> ComparisonResult {
        func split(_ v: String) -> (core: [Int], pre: [String]) {
            let parts = normalize(v).split(separator: "-", maxSplits: 1, omittingEmptySubsequences: false)
            return (parts[0].split(separator: ".").map { Int($0) ?? 0 }, parts.count > 1 ? parts[1].split(separator: ".").map(String.init) : [])
        }
        let x = split(a), y = split(b)
        for i in 0..<max(x.core.count, y.core.count) {
            let l = i < x.core.count ? x.core[i] : 0, r = i < y.core.count ? y.core[i] : 0
            if l != r { return l < r ? .orderedAscending : .orderedDescending }
        }
        if x.pre.isEmpty != y.pre.isEmpty { return x.pre.isEmpty ? .orderedDescending : .orderedAscending }
        for (l, r) in zip(x.pre, y.pre) where l != r {
            switch (Int(l), Int(r)) {
            case let (li?, ri?): return li < ri ? .orderedAscending : .orderedDescending
            case (_?, nil): return .orderedAscending
            case (nil, _?): return .orderedDescending
            default: return l < r ? .orderedAscending : .orderedDescending
            }
        }
        return x.pre.count == y.pre.count ? .orderedSame : x.pre.count < y.pre.count ? .orderedAscending : .orderedDescending
    }

    // MARK: Notes

    /// Notes in the UI language without blocks about features that are off. Only the latest release's notes are known
    /// (not the versions in between), so when everything is filtered out the full list is shown, like UpdatePrompt.Decide.
    public static func visibleNotes(_ markdown: String?, settings: AppSettings, language: String = Loc.current) -> [NoteBlock] {
        let blocks = ReleaseNotes.parse(ReleaseNotes.forLanguage(markdown, language))
        let visible = ReleaseNotes.filter(blocks) { id in
            if id == preciseOcrTag { return false }
            return Feature(rawValue: id).map(settings.isOn) ?? !settings.disabledFeatures.contains(id)
        }
        return visible.isEmpty ? blocks : visible
    }

    // MARK: App flow

    /// Check and react: a newer version opens the update window; an interactive check also reports "up to date" and
    /// errors. AppDelegate's `checkForUpdates(interactive:)` calls this.
    static func run(services: AppServices, interactive: Bool) {
        check(settings: services.settings, interactive: interactive) { result in
            switch result {
            case .available(let r): showWindow(r, services: services, activate: interactive)
            case .skipped: break
            case .upToDate(let v): if interactive { alert(Loc.t("Updates.UpToDateTitle"), "Screeny \(v).") }
            case .failed(let m): if interactive { alert(Loc.t("Updates.CheckFailedTitle"), m) }
            }
        }
    }

    /// First check 30 s after launch, then daily; each one only if `settings.checkForUpdates` is on at that moment.
    /// Copies run outside an app bundle (no version) don't check by themselves.
    static func startBackgroundChecks(services: AppServices) {
        guard timer == nil, AppInfo.bundleVersion != nil else { return }
        let tick = { [weak services] in
            guard let services, services.settings.checkForUpdates, window == nil else { return }
            run(services: services, interactive: false)
        }
        timer = Timer.scheduledTimer(withTimeInterval: interval, repeats: true) { _ in tick() }
        timer?.tolerance = 60 * 60
        DispatchQueue.main.asyncAfter(deadline: .now() + startupDelay) { tick() }
    }

    private static func showWindow(_ release: ReleaseInfo, services: AppServices, activate: Bool) {
        if let w = window, w.release == release { w.show(activate: activate); return }
        window?.close()
        let w = UpdateWindowController(release: release, notes: visibleNotes(release.notes, settings: services.settings), current: AppInfo.bundleVersion) {
            services.settings.skippedUpdateVersion = release.version
            return services.saveSettings()
        }
        w.onClose = { window = nil }
        window = w
        w.show(activate: activate)
    }

    private static func alert(_ title: String, _ text: String) {
        let a = NSAlert()
        a.messageText = title
        a.informativeText = text
        a.addButton(withTitle: Loc.t("Common.OK"))
        NSApp.activate(ignoringOtherApps: true)
        a.runModal()
    }
}

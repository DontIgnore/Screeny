import AppKit

/// Bug report or idea: decides the window texts and the GitHub issue template (Feedback/FeedbackRequest.cs).
public enum FeedbackKind: String, Codable { case bug, idea }

/// Technical details attached to a bug report (`diagnostics`). Same JSON keys as Windows, so the server contract is
/// unchanged; Mac-specific facts (model, every display's scale) travel inside `osVersion`. The log is already scrubbed.
public struct FeedbackDiagnostics: Codable, Equatable {
    public var appVersion: String
    public var osVersion: String
    public var uiLanguage: String
    /// Main display, Windows convention: 96 per 1x (a Retina display is 192).
    public var dpi: Int
    public var monitors: Int
    public var logTail: String
}

/// Body of `POST /api/feedback`: camelCase, kind "bug"/"idea", `"contact": null` written explicitly.
public struct FeedbackRequest: Encodable {
    public var kind: FeedbackKind
    public var title: String
    public var description: String
    public var contact: String?
    public var diagnostics: FeedbackDiagnostics?
    public var installId: String

    /// Trims fields; a blank contact becomes null.
    public init(kind: FeedbackKind, title: String, description: String, contact: String?, diagnostics: FeedbackDiagnostics?, installId: String) {
        self.kind = kind
        self.title = title.trimmingCharacters(in: .whitespacesAndNewlines)
        self.description = description.trimmingCharacters(in: .whitespacesAndNewlines)
        let c = contact?.trimmingCharacters(in: .whitespacesAndNewlines) ?? ""
        self.contact = c.isEmpty ? nil : c
        self.diagnostics = diagnostics
        self.installId = installId
    }

    enum CodingKeys: String, CodingKey { case kind, title, description, contact, diagnostics, installId }
    public func encode(to encoder: Encoder) throws {
        var c = encoder.container(keyedBy: CodingKeys.self)
        try c.encode(kind, forKey: .kind); try c.encode(title, forKey: .title); try c.encode(description, forKey: .description)
        try c.encode(contact, forKey: .contact); try c.encode(diagnostics, forKey: .diagnostics); try c.encode(installId, forKey: .installId)
    }

    public func json() -> Data {
        let enc = JSONEncoder()
        enc.outputFormatting = [.withoutEscapingSlashes]
        return (try? enc.encode(self)) ?? Data()
    }
}

public enum FeedbackFieldError: Equatable { case tooShort, tooLong }

/// Field lengths from the server contract, counted on trimmed text (Feedback/FeedbackRules.cs).
public enum FeedbackRules {
    public static let titleMin = 3, titleMax = 120
    public static let descriptionMin = 10, descriptionMax = 5000
    public static let contactMax = 200

    public static func checkTitle(_ s: String?) -> FeedbackFieldError? { check(s, titleMin, titleMax) }
    public static func checkDescription(_ s: String?) -> FeedbackFieldError? { check(s, descriptionMin, descriptionMax) }
    /// Contact is optional: empty is fine.
    public static func checkContact(_ s: String?) -> FeedbackFieldError? { check(s, 0, contactMax) }
    public static func isValid(title: String?, description: String?, contact: String?) -> Bool {
        checkTitle(title) == nil && checkDescription(description) == nil && checkContact(contact) == nil
    }

    private static func check(_ s: String?, _ min: Int, _ max: Int) -> FeedbackFieldError? {
        // UTF-16 length, like .NET string.Length on the server side.
        let n = s?.trimmingCharacters(in: .whitespacesAndNewlines).utf16.count ?? 0
        return n < min ? .tooShort : n > max ? .tooLong : nil
    }
}

/// Outcome of a send; the window keeps the user's text on every failure.
public enum FeedbackResult: Equatable {
    case sent(id: String?)
    case invalid(message: String?)
    case rateLimited(retryAfter: TimeInterval?)
    case serverError(Int)
    case failed(Int)
    case offline
    /// Dev instance without `--feedback-endpoint`: never touches the network.
    case notConfigured
}

public enum FeedbackEndpoint {
    public static let production = URL(string: "https://screeny.page/api/feedback")!

    /// Release: always production. Dev (`--dev`): only an explicit absolute http(s) `--feedback-endpoint`, else nil, so a
    /// dev run never posts into the real queue.
    public static func choose(isDev: Bool, explicit: String?) -> URL? {
        if !isDev { return production }
        guard let s = explicit?.trimmingCharacters(in: .whitespaces), let u = URL(string: s),
              let scheme = u.scheme?.lowercased(), scheme == "http" || scheme == "https", u.host != nil else { return nil }
        return u
    }

    /// From the process arguments: `--dev` and `--feedback-endpoint <url>`.
    public static func current(_ args: [String] = ProcessInfo.processInfo.arguments) -> URL? {
        let explicit = args.firstIndex(of: "--feedback-endpoint").flatMap { $0 + 1 < args.count ? args[$0 + 1] : nil }
        return choose(isDev: args.contains("--dev"), explicit: explicit)
    }
}

/// POST and response mapping (Feedback/FeedbackClient.cs). Only status codes go to the log, never the text.
public enum FeedbackClient {
    public static let timeout: TimeInterval = 20

    /// Completion on the main queue. Cancelling the returned task (window closed) produces no completion.
    @discardableResult
    public static func send(_ request: FeedbackRequest, to endpoint: URL?, completion: @escaping (FeedbackResult) -> Void) -> URLSessionTask? {
        guard let endpoint else { DispatchQueue.main.async { completion(.notConfigured) }; return nil }
        var req = URLRequest(url: endpoint, timeoutInterval: timeout)
        req.httpMethod = "POST"
        req.setValue("application/json; charset=utf-8", forHTTPHeaderField: "Content-Type")
        req.setValue("Screeny/\(AppInfo.version)", forHTTPHeaderField: "User-Agent")
        req.httpBody = request.json()
        let task = URLSession.shared.dataTask(with: req) { data, response, error in
            if let e = error as? URLError, e.code == .cancelled { return }
            let result: FeedbackResult
            if let http = response as? HTTPURLResponse {
                result = map(status: http.statusCode, body: data.flatMap { String(data: $0, encoding: .utf8) },
                             retryAfterHeader: http.value(forHTTPHeaderField: "Retry-After"), now: Date())
                if case .sent = result {} else { Log.error("Feedback not accepted: HTTP \(http.statusCode)") }
            } else {
                Log.error("Feedback send failed: \((error as? URLError)?.code.rawValue ?? -1)")
                result = .offline
            }
            DispatchQueue.main.async { completion(result) }
        }
        task.resume()
        return task
    }

    public static func map(status: Int, body: String?, retryAfterHeader: String?, now: Date) -> FeedbackResult {
        if (200..<300).contains(status) { return .sent(id: readString(body, "id")) }
        if status == 400 { return .invalid(message: readString(body, "error")) }
        if status == 429 { return .rateLimited(retryAfter: retryAfter(retryAfterHeader, now: now)) }
        return status >= 500 ? .serverError(status) : .failed(status)
    }

    /// Retry-After: seconds or an HTTP date; past dates and negatives become zero.
    public static func retryAfter(_ header: String?, now: Date) -> TimeInterval? {
        guard let h = header?.trimmingCharacters(in: .whitespaces), !h.isEmpty else { return nil }
        if let s = Int(h) { return TimeInterval(max(0, s)) }
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        f.timeZone = TimeZone(identifier: "GMT")
        f.dateFormat = "EEE, dd MMM yyyy HH:mm:ss zzz"
        guard let d = f.date(from: h) else { return nil }
        return max(0, d.timeIntervalSince(now))
    }

    /// A non-blank string field of a JSON object, trimmed; anything else is nil.
    public static func readString(_ json: String?, _ name: String) -> String? {
        guard let data = json?.data(using: .utf8),
              let obj = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let v = (obj[name] as? String)?.trimmingCharacters(in: .whitespacesAndNewlines), !v.isEmpty else { return nil }
        return v
    }

    /// Minutes for "try again in N min": rounded up, at least one. nil when the server didn't say.
    public static func retryMinutes(_ t: TimeInterval?) -> Int? { t.map { max(1, Int(ceil($0 / 60))) } }
}

/// "Open on GitHub instead": the issue form with the title filled in.
public enum GitHubIssueLink {
    public static let newIssueURL = "https://github.com/DontIgnore/Screeny/issues/new"
    public static func build(_ kind: FeedbackKind, title: String?) -> URL {
        var c = URLComponents(string: newIssueURL)!
        c.queryItems = [URLQueryItem(name: "template", value: kind == .bug ? "bug_report.yml" : "feature_request.yml")]
        if let t = title?.trimmingCharacters(in: .whitespacesAndNewlines), !t.isEmpty { c.queryItems!.append(URLQueryItem(name: "title", value: t)) }
        // URLComponents leaves & + # alone in query values: encode the value fully, like Uri.EscapeDataString.
        c.percentEncodedQuery = c.queryItems!.map { "\($0.name)=\($0.value!.addingPercentEncoding(withAllowedCharacters: .alphanumerics.union(CharacterSet(charactersIn: "-._~")))!)" }.joined(separator: "&")
        return c.url!
    }
}

/// Version and identity of this copy.
public enum AppInfo {
    /// CFBundleShortVersionString; nil when run outside an app bundle (swift run).
    public static var bundleVersion: String? { Bundle.main.object(forInfoDictionaryKey: "CFBundleShortVersionString") as? String }
    public static var version: String { bundleVersion ?? "0.0.0" }
    public static let repositoryURL = "https://github.com/DontIgnore/Screeny"
    public static let releasesURL = URL(string: "\(repositoryURL)/releases/latest")!

    /// sysctl hw.model, e.g. "Mac14,2".
    public static var model: String {
        var size = 0
        guard sysctlbyname("hw.model", nil, &size, nil, 0) == 0, size > 0 else { return "unknown" }
        var buf = [CChar](repeating: 0, count: size)
        guard sysctlbyname("hw.model", &buf, &size, nil, 0) == 0 else { return "unknown" }
        return String(cString: buf)
    }
}

/// Collects the details shown and sent with a bug report.
public enum FeedbackDiagnosticsCollector {
    public static func collect() -> FeedbackDiagnostics {
        let v = ProcessInfo.processInfo.operatingSystemVersion
        let build = (NSDictionary(contentsOfFile: "/System/Library/CoreServices/SystemVersion.plist")?["ProductBuildVersion"] as? String).map { ", \($0)" } ?? ""
        #if arch(arm64)
        let arch = "arm64"
        #else
        let arch = "x86_64"
        #endif
        let scales = NSScreen.screens.map { s -> String in
            let x = s.backingScaleFactor
            return x == x.rounded() ? "\(Int(x))x" : "\(x)x"
        }
        let os = "macOS \(v.majorVersion).\(v.minorVersion).\(v.patchVersion) (\(arch)\(build)), \(AppInfo.model), displays \(scales.joined(separator: " "))"
        let mainScale = NSScreen.main?.backingScaleFactor ?? 1
        return FeedbackDiagnostics(appVersion: AppInfo.version, osVersion: os, uiLanguage: Loc.current,
                                   dpi: Int((96 * mainScale).rounded()), monitors: max(1, NSScreen.screens.count),
                                   logTail: LogScrubber.readTail(Log.url, context: .current()))
    }
}

/// What to replace in the log: this account's home folder and user name (Feedback/LogScrubber.cs).
public struct ScrubContext {
    public var home: String?
    public var userName: String?
    public init(home: String?, userName: String?) { self.home = home; self.userName = userName }
    public static func current() -> ScrubContext { ScrubContext(home: NSHomeDirectory(), userName: NSUserName()) }
}

/// Log tail for the technical details. Removes what points to the person: the home folder → `~`, any other folder
/// inside /Users/ → `/Users/<user>`, the user name as a whole path component → `<user>`; lines carrying recognized
/// screenshot text are dropped entirely. Long lines are cut.
public enum LogScrubber {
    public static let tailLines = 100
    public static let maxLineLength = 500
    public static let maxReadBytes = 128 * 1024
    public static let homeToken = "~"
    public static let userToken = "<user>"
    public static let removedLine = "[line removed: recognized text]"
    static let droppedMarkers = ["Dev text mode:"]

    private static let usersFolder = try! NSRegularExpression(pattern: #"(?<=/Users/)(?!<user>)[^/\s"'<>|*?:]+"#, options: [.caseInsensitive])

    public static func scrub(_ line: String, _ ctx: ScrubContext) -> String {
        if droppedMarkers.contains(where: line.contains) { return removedLine }
        var text = line
        func replace(_ pattern: String, _ with: String) {
            let rx = try! NSRegularExpression(pattern: pattern, options: [.caseInsensitive])
            text = rx.stringByReplacingMatches(in: text, range: NSRange(text.startIndex..., in: text), withTemplate: NSRegularExpression.escapedTemplate(for: with))
        }
        let end = #"(?=[/"'\s:,)]|$)"#
        if let home = ctx.home?.trimmingCharacters(in: CharacterSet(charactersIn: "/")), !home.isEmpty {
            replace("/" + NSRegularExpression.escapedPattern(for: home) + end, homeToken)
        }
        text = usersFolder.stringByReplacingMatches(in: text, range: NSRange(text.startIndex..., in: text), withTemplate: userToken)
        if let name = ctx.userName, name.count >= 2, name != userToken {
            replace("(?<=/)" + NSRegularExpression.escapedPattern(for: name) + end, userToken)
        }
        return text.count > maxLineLength ? String(text.prefix(maxLineLength)) + "…" : text
    }

    public static func tail(_ lines: [String], _ ctx: ScrubContext, count: Int = tailLines) -> String {
        lines.suffix(count).map { scrub($0, ctx) }.joined(separator: "\n")
    }

    /// Scrubbed tail of the log file; empty if there is no log or it can't be read.
    public static func readTail(_ url: URL, context: ScrubContext, count: Int = tailLines) -> String {
        guard let h = try? FileHandle(forReadingFrom: url) else { return "" }
        defer { try? h.close() }
        let size = (try? h.seekToEnd()) ?? 0
        let cut = size > UInt64(maxReadBytes)
        try? h.seek(toOffset: cut ? size - UInt64(maxReadBytes) : 0)
        guard let data = try? h.readToEnd() else { return "" }
        var lines = String(decoding: data, as: UTF8.self).replacingOccurrences(of: "\r\n", with: "\n").components(separatedBy: "\n")
        if cut, !lines.isEmpty { lines.removeFirst() } // first line was read from the middle
        if lines.last == "" { lines.removeLast() }
        return tail(lines, context, count: count)
    }
}

import Foundation

/// Features that can be turned off entirely (Features/FeatureCatalog.cs).
public enum Feature: String, CaseIterable {
    case ocr, history, pins, beautify, pipette, ruler
    public var title: String { Loc.t("Feature.\(key).Title") }
    public var details: String { Loc.t("Feature.\(key).Description") }
    private var key: String { rawValue == "ocr" ? "Ocr" : rawValue.prefix(1).uppercased() + rawValue.dropFirst() }
}

public enum CaptureAction { case copy, copyExplicit, save, pin, copyText }

/// A global shortcut: virtual key code + Carbon-free modifier flags (NSEvent.ModifierFlags raw values).
public struct HotkeySpec: Codable, Equatable, Hashable {
    public var keyCode: UInt16
    public var modifiers: UInt
    public var enabled = true
    public init(keyCode: UInt16, modifiers: UInt, enabled: Bool = true) { self.keyCode = keyCode; self.modifiers = modifiers; self.enabled = enabled }
}

/// Settings stored in ~/Library/Application Support/Screeny/settings.json (Settings/AppSettings.cs).
public final class AppSettings: Codable {
    public static let minHistoryLimit = 1, maxHistoryLimit = 500
    public static let minThickness = 1, maxThickness = 20
    public static let minFillOpacity = 10, maxFillOpacity = 100

    public var hotkeys: [HotkeySpec] = AppSettings.defaultHotkeys
    public var historyFolder: String = AppSettings.defaultHistoryFolder
    public var historyLimit = 50
    public var copyOnCapture = true
    public var startAtLogin = false
    public var lastColor = "#FF3B30"
    public var lastThickness = 3
    public var excludePinsFromCapture = false
    public var keepOriginals = false
    public var snapEnabled = true
    public var checkForUpdates = true
    public var setupWizardVersion = 0
    public var disabledFeatures: [String] = []
    public var language = Loc.auto
    public var installId: String?

    public var lastStrokeEnabled = true
    public var lastFillEnabled = false
    public var lastFillOpacity = 40
    public var lastArrowStyle = ArrowStyle.normal
    public var lastTextBackground = false
    public var lastTextAlign = TextAlign.left

    public var beautifyEnabled = false
    public var beautifyPreset = BeautifyOptions.default.preset
    public var beautifyPadding = BeautifyOptions.default.padding
    public var beautifyRadius = BeautifyOptions.default.radius
    public var beautifyShadow = BeautifyOptions.default.shadow
    public var skippedUpdateVersion: String?

    /// ⌘⇧2 by default: free on a stock Mac (⌘⇧3/4/5 belong to the system screenshot tool).
    public static let defaultHotkeys = [HotkeySpec(keyCode: 19, modifiers: (1 << 20) | (1 << 17))]

    public static var supportFolder: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0].appendingPathComponent("Screeny", isDirectory: true)
    }
    public static var defaultHistoryFolder: String { supportFolder.appendingPathComponent("History", isDirectory: true).path }

    public init() {}

    public required init(from decoder: Decoder) throws {
        // Missing or broken fields keep their defaults: an old or hand-edited file never blocks startup.
        let c = try decoder.container(keyedBy: CodingKeys.self)
        func v<T: Decodable>(_ k: CodingKeys, _ d: T) -> T { (try? c.decodeIfPresent(T.self, forKey: k)) ?? d }
        let d = AppSettings()
        hotkeys = v(.hotkeys, d.hotkeys); historyFolder = v(.historyFolder, d.historyFolder); historyLimit = v(.historyLimit, d.historyLimit)
        copyOnCapture = v(.copyOnCapture, d.copyOnCapture); startAtLogin = v(.startAtLogin, d.startAtLogin)
        lastColor = v(.lastColor, d.lastColor); lastThickness = v(.lastThickness, d.lastThickness)
        excludePinsFromCapture = v(.excludePinsFromCapture, d.excludePinsFromCapture); keepOriginals = v(.keepOriginals, d.keepOriginals)
        snapEnabled = v(.snapEnabled, d.snapEnabled); checkForUpdates = v(.checkForUpdates, d.checkForUpdates)
        setupWizardVersion = v(.setupWizardVersion, d.setupWizardVersion); disabledFeatures = v(.disabledFeatures, d.disabledFeatures)
        language = v(.language, d.language); installId = v(.installId, d.installId)
        lastStrokeEnabled = v(.lastStrokeEnabled, d.lastStrokeEnabled); lastFillEnabled = v(.lastFillEnabled, d.lastFillEnabled)
        lastFillOpacity = v(.lastFillOpacity, d.lastFillOpacity); lastArrowStyle = v(.lastArrowStyle, d.lastArrowStyle)
        lastTextBackground = v(.lastTextBackground, d.lastTextBackground); lastTextAlign = v(.lastTextAlign, d.lastTextAlign)
        beautifyEnabled = v(.beautifyEnabled, d.beautifyEnabled); beautifyPreset = v(.beautifyPreset, d.beautifyPreset)
        beautifyPadding = v(.beautifyPadding, d.beautifyPadding); beautifyRadius = v(.beautifyRadius, d.beautifyRadius)
        beautifyShadow = v(.beautifyShadow, d.beautifyShadow); skippedUpdateVersion = v(.skippedUpdateVersion, d.skippedUpdateVersion)
        normalize()
    }

    @discardableResult public func normalize() -> AppSettings {
        var seen = Set<HotkeySpec>()
        hotkeys = hotkeys.filter { seen.insert(HotkeySpec(keyCode: $0.keyCode, modifiers: $0.modifiers)).inserted && $0.modifiers != 0 }
        if historyFolder.isEmpty || !historyFolder.hasPrefix("/") { historyFolder = AppSettings.defaultHistoryFolder }
        if RGBA.parse(lastColor) == nil { lastColor = "#FF3B30" }
        historyLimit = clamp(historyLimit, Self.minHistoryLimit, Self.maxHistoryLimit)
        lastThickness = clamp(lastThickness, Self.minThickness, Self.maxThickness)
        lastFillOpacity = clamp(lastFillOpacity, Self.minFillOpacity, Self.maxFillOpacity)
        if !lastStrokeEnabled && !lastFillEnabled { lastStrokeEnabled = true }
        beautifyRadius = BeautifyOptions.snapRadius(beautifyRadius)
        if setupWizardVersion < 0 { setupWizardVersion = 0 }
        language = Loc.normalizeSetting(language)
        disabledFeatures = Array(Set(disabledFeatures.map { $0.trimmingCharacters(in: .whitespaces).lowercased() }.filter { !$0.isEmpty })).sorted()
        return self
    }

    public func isOn(_ f: Feature) -> Bool { !disabledFeatures.contains(f.rawValue) }
    public func set(_ f: Feature, on: Bool) {
        disabledFeatures.removeAll { $0 == f.rawValue }
        if !on { disabledFeatures.append(f.rawValue) }
    }

    public func actionAllowed(_ a: CaptureAction) -> Bool {
        switch a { case .pin: return isOn(.pins); case .copyText: return isOn(.ocr); default: return true }
    }

    public func toolAllowed(_ t: ToolKind) -> Bool { t != .ruler || isOn(.ruler) }

    public var beautify: BeautifyOptions {
        get { BeautifyOptions(preset: beautifyPreset, padding: beautifyPadding, radius: BeautifyOptions.snapRadius(beautifyRadius), shadow: beautifyShadow) }
        set { beautifyPreset = newValue.preset; beautifyPadding = newValue.padding; beautifyRadius = newValue.radius; beautifyShadow = newValue.shadow }
    }

    public func copy() -> AppSettings {
        let data = try! JSONEncoder().encode(self)
        return try! JSONDecoder().decode(AppSettings.self, from: data)
    }

    // MARK: Store

    public static var fileURL: URL { supportFolder.appendingPathComponent("settings.json") }

    public static func load(from url: URL = fileURL) -> AppSettings {
        guard let data = try? Data(contentsOf: url) else { return AppSettings() }
        do { return try JSONDecoder().decode(AppSettings.self, from: data) } catch {
            Log.error("Settings file is unreadable; using defaults", error)
            try? FileManager.default.moveItem(at: url, to: url.appendingPathExtension("bad"))
            return AppSettings()
        }
    }

    /// Atomic write: a crash mid-save never leaves a half-written file.
    public func save(to url: URL = fileURL) throws {
        try FileManager.default.createDirectory(at: url.deletingLastPathComponent(), withIntermediateDirectories: true)
        let enc = JSONEncoder()
        enc.outputFormatting = [.prettyPrinted, .sortedKeys]
        try enc.encode(self).write(to: url, options: .atomic)
    }
}

/// Error log at ~/Library/Logs/Screeny/screeny.log (Logging/Log.cs).
public enum Log {
    static let queue = DispatchQueue(label: "screeny.log")
    public static var url: URL {
        FileManager.default.urls(for: .libraryDirectory, in: .userDomainMask)[0].appendingPathComponent("Logs/Screeny/screeny.log")
    }

    public static func error(_ message: String, _ error: Error? = nil) {
        let line = "\(ISO8601DateFormatter().string(from: Date())) \(message)\(error.map { ": \($0)" } ?? "")\n"
        #if DEBUG
        FileHandle.standardError.write(Data(line.utf8))
        #endif
        queue.async {
            let u = url
            try? FileManager.default.createDirectory(at: u.deletingLastPathComponent(), withIntermediateDirectories: true)
            if let h = try? FileHandle(forWritingTo: u) {
                h.seekToEndOfFile(); h.write(Data(line.utf8)); try? h.close()
                if (try? u.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0 > 1_000_000 {
                    try? FileManager.default.removeItem(at: u.appendingPathExtension("old"))
                    try? FileManager.default.moveItem(at: u, to: u.appendingPathExtension("old"))
                }
            } else {
                try? Data(line.utf8).write(to: u)
            }
        }
    }

    public static func tail(lines: Int = 40) -> String {
        guard let s = try? String(contentsOf: url, encoding: .utf8) else { return "" }
        return s.split(separator: "\n", omittingEmptySubsequences: false).suffix(lines).joined(separator: "\n")
    }
}

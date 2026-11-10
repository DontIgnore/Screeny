import Foundation

/// Interface strings: the same keys and texts as the Windows build (Strings*.resx → Resources/Strings/<lang>.json),
/// plus macOS overrides (Resources/Strings/mac/<lang>.json) for texts that name Windows things.
public enum Loc {
    public static let auto = "auto"
    public static let languages = ["en", "ru", "es", "zh-Hans", "ja"]
    public static let changed = Notification.Name("ScreenyLanguageChanged")

    private static var table: [String: String] = [:]
    private static var english: [String: String] = [:]
    public private(set) static var current = "en"

    static let bundle: Bundle = .module

    private static func load(_ lang: String) -> [String: String] {
        var result: [String: String] = [:]
        for path in ["Strings/\(lang).json", "Strings/mac/\(lang).json"] {
            guard let url = bundle.resourceURL?.appendingPathComponent(path),
                  let data = try? Data(contentsOf: url),
                  let dict = try? JSONSerialization.jsonObject(with: data) as? [String: String] else { continue }
            result.merge(dict) { _, new in new }
        }
        return result
    }

    /// Language from the setting: "auto" follows the system's preferred languages, falling back to English.
    public static func resolve(_ setting: String) -> String {
        if languages.contains(setting) { return setting }
        for pref in Locale.preferredLanguages {
            let id = pref.lowercased()
            if id.hasPrefix("zh-hans") || id == "zh-cn" || id.hasPrefix("zh-sg") || id == "zh" { return "zh-Hans" }
            for l in languages where l != "zh-Hans" && (id == l || id.hasPrefix(l + "-")) { return l }
        }
        return "en"
    }

    public static func normalizeSetting(_ s: String?) -> String {
        guard let s, languages.contains(s) else { return auto }
        return s
    }

    public static func setLanguage(_ setting: String) {
        let lang = resolve(setting)
        if english.isEmpty { english = load("en") }
        let changed = lang != current || table.isEmpty
        current = lang
        table = lang == "en" ? english : load(lang)
        if changed { NotificationCenter.default.post(name: Loc.changed, object: nil) }
    }

    public static func t(_ key: String) -> String {
        if table.isEmpty { setLanguage(auto) }
        return macify(table[key] ?? english[key] ?? key)
    }

    /// Windows-style "{0}" placeholders.
    public static func f(_ key: String, _ args: CustomStringConvertible...) -> String {
        var s = t(key)
        for (i, a) in args.enumerated() { s = s.replacingOccurrences(of: "{\(i)}", with: a.description) }
        return s
    }

    /// Native name of a language for the picker.
    public static func nativeName(_ lang: String) -> String {
        switch lang {
        case "ru": return "Русский"
        case "es": return "Español"
        case "zh-Hans": return "简体中文"
        case "ja": return "日本語"
        default: return "English"
        }
    }

    /// Key names in texts follow Mac conventions: Ctrl+C → ⌘C, Alt → ⌥, Shift+ → ⇧.
    static func macify(_ s: String) -> String {
        guard s.contains("Ctrl") || s.contains("Alt") || s.contains("Shift+") else { return s }
        var r = s
        for (pattern, template) in macRules {
            r = pattern.stringByReplacingMatches(in: r, range: NSRange(r.startIndex..., in: r), withTemplate: template)
        }
        return r
    }

    private static let macRules: [(NSRegularExpression, String)] = [
        ("\\bCtrl\\+Shift\\+", "⇧⌘"), ("\\bCtrl\\+(wheel|колес|rueda)", "⌘+$1"), ("\\bCtrl\\+", "⌘"), ("\\bCtrl\\b", "⌘"),
        ("\\bShift\\+", "⇧"), ("\\bAlt\\+", "⌥"), ("\\bAlt\\b", "⌥ Option"),
    ].map { (try! NSRegularExpression(pattern: $0.0), $0.1) }
}

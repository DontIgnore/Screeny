import Foundation

public enum NoteBlockKind: Equatable { case heading1, heading2, heading3, bullet, numbered, paragraph }

/// A run of text: plain, bold or code.
public struct NoteInline: Equatable {
    public var text: String
    public var bold = false
    public var code = false
    public init(_ text: String, bold: Bool = false, code: Bool = false) { self.text = text; self.bold = bold; self.code = code }
}

public struct NoteBlock: Equatable {
    public var kind: NoteBlockKind
    public var inlines: [NoteInline]
    /// Number of a numbered list item.
    public var number = 0
    /// List nesting (0 = top level).
    public var level = 0
    /// Features the block is about; empty = general, always shown. Shown if at least one is on.
    public var features: [String] = []
    /// Heading depth in the source (number of `#`, 1–6); 0 = not a heading.
    public var depth = 0
    public var plainText: String { inlines.map(\.text).joined() }
}

/// Tiny markdown subset for release notes (Updates/ReleaseNotes.cs): `#`–`###` headings (deeper shown as `###`), bullet
/// (`-*+`) and numbered lists, bold (`**`, `__`), `` `code` ``. Links show as text only, italics are dropped, HTML is not
/// interpreted except `<!-- -->` comments, which are hidden. `<!-- feature: ocr, history -->` tags a heading (its whole
/// section) or item; a tag on its own line applies to the next block. `<!-- lang: xx -->` lines split the file into
/// language sections; text before the first one is common to all languages.
public enum ReleaseNotes {
    private static func rx(_ p: String, _ o: NSRegularExpression.Options = []) -> NSRegularExpression { try! NSRegularExpression(pattern: p, options: o) }
    private static let headingRx = rx(#"^(#{1,6})\s+(.*?)\s*#*\s*$"#)
    private static let bulletRx = rx(#"^(\s*)[-*+]\s+(.*)$"#)
    private static let numberedRx = rx(#"^(\s*)(\d{1,4})[.)]\s+(.*)$"#)
    private static let ruleRx = rx(#"^\s*([-*_])(\s*\1){2,}\s*$"#)
    private static let featureTagRx = rx(#"^\s*feature\s*:(.*)$"#, [.caseInsensitive, .dotMatchesLineSeparators])
    private static let langTagRx = rx(#"^ {0,3}<!--\s*lang\s*:\s*([A-Za-z]{2,3}(?:-[A-Za-z0-9]{2,8})*)\s*-->\s*$"#, .caseInsensitive)
    /// Characters a backslash escapes.
    private static let escapable = Set("\\`*_[]()#+-.!<>")

    /// Capture groups of a whole-line match (nil when no match).
    private static func match(_ r: NSRegularExpression, _ s: String) -> [String]? {
        guard let m = r.firstMatch(in: s, range: NSRange(s.startIndex..., in: s)) else { return nil }
        return (0..<m.numberOfRanges).map { Range(m.range(at: $0), in: s).map { String(s[$0]) } ?? "" }
    }

    private static func lines(_ s: String) -> [String] {
        s.replacingOccurrences(of: "\r\n", with: "\n").replacingOccurrences(of: "\r", with: "\n").components(separatedBy: "\n")
    }

    // MARK: Languages

    public static func languages(_ markdown: String?) -> [String] {
        var seen = Set<String>()
        return sections(markdown).sections.map(\.lang).filter { seen.insert($0.lowercased()).inserted }
    }

    /// The section for `language` (one of Loc.languages), preceded by the common text: exact tag first, then a tag that
    /// serves it ("zh", "zh-CN" → zh-Hans; "en-US" → en); else English the same way; else the first section.
    public static func forLanguage(_ markdown: String?, _ language: String) -> String? {
        let (common, secs) = sections(markdown)
        if secs.isEmpty { return markdown }
        func find(_ lang: String) -> Int? {
            secs.firstIndex { $0.lang.caseInsensitiveCompare(lang) == .orderedSame } ?? secs.firstIndex { serves($0.lang, lang) }
        }
        return common + secs[find(language) ?? find("en") ?? 0].text
    }

    private static func serves(_ tag: String, _ language: String) -> Bool {
        let t = tag.lowercased(), l = language.lowercased()
        if l.hasPrefix(t + "-") { return true }
        if t.hasPrefix("zh-hans") || t == "zh-cn" || t == "zh-sg" { return language == "zh-Hans" }
        if t.hasPrefix("zh") { return false } // zh-Hant, zh-TW: not Simplified
        return t.hasPrefix(l + "-") // en-US → en
    }

    private static func sections(_ markdown: String?) -> (common: String, sections: [(lang: String, text: String)]) {
        guard let markdown, !markdown.isEmpty else { return ("", []) }
        var secs: [(lang: String, text: String)] = []
        var common = ""
        var inFence = false, inComment = false
        for line in lines(markdown) {
            if !inComment && line.trimmingCharacters(in: .whitespaces).hasPrefix("```") { inFence.toggle() }
            else if !inFence && !inComment, let tag = match(langTagRx, line) { secs.append((tag[1], "")); continue }
            else if !inFence { var rest = line; _ = stripComments(&rest, &inComment) }
            if secs.isEmpty { common += line + "\n" } else { secs[secs.count - 1].text += line + "\n" }
        }
        return (common, secs)
    }

    // MARK: Parse

    public static func parse(_ markdown: String?) -> [NoteBlock] {
        var blocks: [NoteBlock] = []
        guard let markdown, !markdown.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else { return blocks }
        var paragraph = ""
        var paragraphTag: [String]?
        var pendingTag: [String]? // tag on its own line, for the next block
        var inFence = false, inComment = false
        var headings: [(level: Int, features: [String])] = []
        var items: [(level: Int, features: [String])] = []
        func headingScope() -> [String] { headings.last?.features ?? [] }

        func flushParagraph() {
            if paragraph.isEmpty { return }
            blocks.append(NoteBlock(kind: .paragraph, inlines: parseInlines(paragraph), features: paragraphTag ?? headingScope()))
            paragraph = ""
            paragraphTag = nil
            items.removeAll() // a paragraph ends the list
        }
        // Own tag, else the tag of the enclosing item, else of the heading.
        func itemFeatures(_ level: Int, _ own: [String]?) -> [String] {
            items.removeAll { $0.level >= level }
            let f = own ?? items.last?.features ?? headingScope()
            items.append((level, f))
            return f
        }

        for rawLine in lines(markdown) {
            var line = rawLine.replacingOccurrences(of: #"\s+$"#, with: "", options: .regularExpression)
            if !inComment && line.trimmingCharacters(in: .whitespaces).hasPrefix("```") { flushParagraph(); inFence.toggle(); continue }
            if inFence {
                if !line.isEmpty {
                    blocks.append(NoteBlock(kind: .paragraph, inlines: [NoteInline(line.trimmingCharacters(in: .whitespaces), code: true)], features: headingScope()))
                }
                continue
            }
            let hadComment = inComment || line.contains("<!--")
            var tag = stripComments(&line, &inComment)
            if hadComment && line.trimmingCharacters(in: .whitespaces).isEmpty {
                if let tag { pendingTag = tag }
                continue
            }
            if line.isEmpty || match(ruleRx, line) != nil { flushParagraph(); continue }
            let isList = match(bulletRx, line) != nil || match(numberedRx, line) != nil
            let continuation = (rawLine.first?.isWhitespace ?? false) && paragraph.isEmpty && !blocks.isEmpty
                && (blocks.last!.kind == .bullet || blocks.last!.kind == .numbered) && match(headingRx, line) == nil && !isList
            if !continuation && paragraph.isEmpty, let p = pendingTag {
                tag = tag ?? p
                pendingTag = nil
            }

            if let h = match(headingRx, line) {
                flushParagraph()
                let depth = h[1].count
                headings.removeAll { $0.level >= depth }
                let f = tag ?? headingScope()
                headings.append((depth, f))
                items.removeAll()
                blocks.append(NoteBlock(kind: depth == 1 ? .heading1 : depth == 2 ? .heading2 : .heading3, inlines: parseInlines(h[2]), features: f, depth: depth))
            } else if let b = match(bulletRx, line) {
                flushParagraph()
                let level = indentLevel(b[1])
                blocks.append(NoteBlock(kind: .bullet, inlines: parseInlines(b[2]), level: level, features: itemFeatures(level, tag)))
            } else if let n = match(numberedRx, line) {
                flushParagraph()
                let level = indentLevel(n[1])
                blocks.append(NoteBlock(kind: .numbered, inlines: parseInlines(n[3]), number: Int(n[2]) ?? 0, level: level, features: itemFeatures(level, tag)))
            } else if continuation {
                // Item continued on an indented line; a tag here tags the item.
                var last = blocks.removeLast()
                last.inlines = merge(last.inlines + [NoteInline(" ")] + parseInlines(line.trimmingCharacters(in: .whitespaces)))
                if let tag { last.features = tag; if !items.isEmpty { items[items.count - 1].features = tag } }
                blocks.append(last)
            } else {
                if !paragraph.isEmpty { paragraph += " " }
                paragraph += line.trimmingCharacters(in: .whitespaces)
                if let tag { paragraphTag = tag }
            }
        }
        flushParagraph()
        return blocks
    }

    /// Removes `<!-- -->` comments (and the start of a multi-line one); returns ids from `feature:` tags, nil if none.
    /// `<!--` inside `` `code` `` or escaped is plain text.
    private static func stripComments(_ line: inout String, _ inComment: inout Bool) -> [String]? {
        let c = Array(line)
        var i = 0
        func find(_ s: String, from: Int) -> Int? {
            let p = Array(s)
            guard from <= c.count - p.count else { return nil }
            return (from...(c.count - p.count)).first { Array(c[$0..<$0 + p.count]) == p }
        }
        if inComment {
            guard let close = find("-->", from: 0) else { line = ""; return nil }
            i = close + 3
            inComment = false
        }
        if i == 0 && !line.contains("<!--") { return nil }
        var text = ""
        var features: [String] = []
        while i < c.count {
            let ch = c[i]
            if ch == "\\" && i + 1 < c.count && escapable.contains(c[i + 1]) {
                text.append(ch); text.append(c[i + 1]); i += 2
            } else if ch == "`", let end = c[(i + 1)...].firstIndex(of: "`") {
                text += String(c[i...end]); i = end + 1
            } else if c.count - i >= 4 && String(c[i..<i + 4]) == "<!--" {
                guard let close = find("-->", from: i + 4) else { inComment = true; break }
                if let t = match(featureTagRx, String(c[(i + 4)..<close])) {
                    for id in t[1].split(separator: ",").map({ $0.trimmingCharacters(in: .whitespaces).lowercased() }) where !id.isEmpty && !features.contains(id) {
                        features.append(id)
                    }
                }
                i = close + 3
            } else {
                text.append(ch); i += 1
            }
        }
        line = text.replacingOccurrences(of: #"\s+$"#, with: "", options: .regularExpression)
        return features.isEmpty ? nil : features
    }

    // MARK: Filter

    /// Drops blocks whose features are all off, then headings left with nothing under them. A heading is judged by its
    /// content only: an item with its own tag kept under a disabled heading keeps the heading.
    public static func filter(_ blocks: [NoteBlock], isOn: (String) -> Bool) -> [NoteBlock] {
        let kept = blocks.filter { headingRank($0) != nil || $0.features.isEmpty || $0.features.contains(where: isOn) }
        return kept.indices.filter { i in
            guard let rank = headingRank(kept[i]) else { return true }
            for j in (i + 1)..<max(i + 1, kept.count) {
                guard let next = headingRank(kept[j]) else { return true }
                if next <= rank { return false }
            }
            return false
        }.map { kept[$0] }
    }

    public static func isTagged(_ blocks: [NoteBlock]) -> Bool { blocks.contains { !$0.features.isEmpty } }

    private static func headingRank(_ b: NoteBlock) -> Int? {
        if b.depth > 0, [.heading1, .heading2, .heading3].contains(b.kind) { return b.depth }
        switch b.kind {
        case .heading1: return 1
        case .heading2: return 2
        case .heading3: return 3
        default: return nil
        }
    }

    /// Two spaces (or a tab = 4) per level; at most two levels.
    private static func indentLevel(_ indent: String) -> Int { min(indent.reduce(0) { $0 + ($1 == "\t" ? 4 : 1) } / 2, 2) }

    // MARK: Inlines

    /// Bold, code, links (text only); `\*` escapes; single `*`/`_` italics markers are dropped.
    public static func parseInlines(_ s: String) -> [NoteInline] {
        let t = Array(s)
        var result: [NoteInline] = []
        var current = ""
        var bold = false
        func flush() { if !current.isEmpty { result.append(NoteInline(current, bold: bold)); current = "" } }
        func isWord(_ c: Character) -> Bool { c.isLetter || c.isNumber }
        var i = 0
        while i < t.count {
            let c = t[i]
            if c == "\\" && i + 1 < t.count && escapable.contains(t[i + 1]) {
                i += 1; current.append(t[i])
            } else if c == "`", let end = t[(i + 1)...].firstIndex(of: "`") {
                flush()
                result.append(NoteInline(String(t[(i + 1)..<end]), bold: bold, code: true))
                i = end
            } else if (c == "*" || c == "_") && i + 1 < t.count && t[i + 1] == c {
                // "__" inside a word (snake__case) is not markup.
                if c == "_" && i > 0 && isWord(t[i - 1]) && i + 2 < t.count && isWord(t[i + 2]) { current += "__"; i += 2; continue }
                flush()
                bold.toggle()
                i += 1
            } else if c == "*" || (c == "_" && (i == 0 || !isWord(t[i - 1]) || i + 1 >= t.count || !isWord(t[i + 1]))) {
                // Italics are not shown: just drop the marker.
            } else if c == "[", let close = t[(i + 1)...].firstIndex(of: "]"), close + 1 < t.count, t[close + 1] == "(",
                      let paren = t[(close + 2)...].firstIndex(of: ")") {
                for inline in parseInlines(String(t[(i + 1)..<close])) {
                    flush()
                    var x = inline; x.bold = x.bold || bold; result.append(x)
                }
                i = paren
            } else {
                current.append(c)
            }
            i += 1
        }
        flush()
        return merge(result)
    }

    /// Joins neighbouring runs with the same style.
    private static func merge(_ inlines: [NoteInline]) -> [NoteInline] {
        var out: [NoteInline] = []
        for x in inlines where !x.text.isEmpty {
            if let last = out.last, last.bold == x.bold, last.code == x.code { out[out.count - 1].text += x.text } else { out.append(x) }
        }
        return out
    }
}

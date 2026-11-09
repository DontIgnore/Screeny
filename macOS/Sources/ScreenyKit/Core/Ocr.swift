import CoreGraphics
import Foundation
import Vision

public struct OcrWord: Equatable { public var text: String; public var box: CGRect }
public struct OcrLine: Equatable { public var words: [OcrWord]; public var text: String; public var noSpaces = false }

/// Recognized words in reading order with selection helpers (Ocr/OcrLayout.cs).
public final class OcrLayout {
    public let lines: [OcrLine]
    private let index: [(line: Int, word: Int)]

    public init(_ lines: [OcrLine]) {
        self.lines = lines.filter { !$0.words.isEmpty }
        var idx: [(Int, Int)] = []
        for (l, line) in self.lines.enumerated() { for w in line.words.indices { idx.append((l, w)) } }
        index = idx
    }

    public static let empty = OcrLayout([])
    public var wordCount: Int { index.count }
    public var isEmpty: Bool { index.isEmpty }
    public func word(_ i: Int) -> OcrWord { lines[index[i].line].words[index[i].word] }
    public func lineOf(_ i: Int) -> Int { index[i].line }

    public func map(offset: CGVector) -> OcrLayout {
        OcrLayout(lines.map { l in OcrLine(words: l.words.map { OcrWord(text: $0.text, box: $0.box.offset(offset)) }, text: l.text, noSpaces: l.noSpaces) })
    }

    public func wordAt(_ p: CGPoint, tolerance t: Double = 3) -> Int? {
        var best: Int?, bestD = Double.greatestFiniteMagnitude
        for i in 0..<wordCount {
            let b = word(i).box
            guard b.insetBy(dx: -t, dy: -t).containsInclusive(p) else { continue }
            let dx = max(0, max(b.minX - p.x, p.x - b.maxX)), dy = max(0, max(b.minY - p.y, p.y - b.maxY))
            let d = dx * dx + dy * dy
            if d < bestD { bestD = d; best = i }
        }
        return best
    }

    public func range(_ a: Int, _ b: Int) -> Set<Int> { Set(min(a, b)...max(a, b)) }
    public func inRect(_ r: CGRect) -> Set<Int> { Set((0..<wordCount).filter { word($0).box.intersects(r) }) }
    public func all() -> Set<Int> { Set(0..<wordCount) }

    public func lineRange(_ i: Int) -> Set<Int> {
        let l = lineOf(i)
        var first = i, last = i
        while first > 0 && lineOf(first - 1) == l { first -= 1 }
        while last < wordCount - 1 && lineOf(last + 1) == l { last += 1 }
        return range(first, last)
    }

    public static func lineBox(_ line: OcrLine) -> CGRect { line.words.dropFirst().reduce(line.words[0].box) { $0.union($1.box) } }

    public func textOf(_ selection: Set<Int>?) -> String {
        var parts: [String] = []
        var i = 0
        for line in lines {
            var picked: [String] = []
            for w in line.words {
                if selection == nil || selection!.isEmpty || selection!.contains(i) { picked.append(w.text) }
                i += 1
            }
            if picked.isEmpty { continue }
            parts.append(picked.count == line.words.count && !line.text.isEmpty ? line.text : picked.joined(separator: line.noSpaces ? "" : " "))
        }
        return parts.joined(separator: "\n")
    }

    /// Runs of adjacent selected words on the same line, for highlighting.
    public func selectionRuns(_ sel: Set<Int>) -> [CGRect] {
        var out: [CGRect] = []
        var run: CGRect?, runLine = -1, prev = -2
        for i in 0..<wordCount where sel.contains(i) {
            let b = word(i).box
            if let r = run, lineOf(i) == runLine, prev == i - 1 { run = r.union(b) } else {
                if let r = run { out.append(r) }
                run = b; runLine = lineOf(i)
            }
            prev = i
        }
        if let r = run { out.append(r) }
        return out
    }
}

public struct DetectedCode: Equatable {
    public var format: String
    public var text: String
    public var box: CGRect
    public var isLink: Bool {
        guard let u = URL(string: text.trimmingCharacters(in: .whitespacesAndNewlines)), let s = u.scheme?.lowercased() else { return false }
        return (s == "http" || s == "https") && u.host != nil
    }
}

/// Text and code recognition with Vision: runs fully on the Mac.
public enum OcrService {
    /// Words with boxes in image pixels (y-down). CJK lines are kept without spaces.
    public static func recognize(_ image: CGImage) throws -> OcrLayout {
        let req = VNRecognizeTextRequest()
        req.recognitionLevel = .accurate
        req.usesLanguageCorrection = true
        if #available(macOS 13, *) { req.automaticallyDetectsLanguage = true }
        req.recognitionLanguages = preferredLanguages(for: req)
        try VNImageRequestHandler(cgImage: image, options: [:]).perform([req])
        let w = CGFloat(image.width), h = CGFloat(image.height)
        func toPixels(_ r: CGRect) -> CGRect { CGRect(x: r.minX * w, y: (1 - r.maxY) * h, width: r.width * w, height: r.height * h) }
        var lines: [OcrLine] = []
        for obs in req.results ?? [] {
            guard let cand = obs.topCandidates(1).first else { continue }
            let text = cand.string
            let cjk = text.unicodeScalars.contains { isCJK($0) }
            var words: [OcrWord] = []
            if cjk {
                // No spaces between words: each run of characters is one selectable unit.
                for (r, piece) in tokens(text, cjk: true) {
                    if let box = try? cand.boundingBox(for: r)?.boundingBox { words.append(OcrWord(text: piece, box: toPixels(box))) }
                }
            } else {
                for (r, piece) in tokens(text, cjk: false) {
                    if let box = try? cand.boundingBox(for: r)?.boundingBox { words.append(OcrWord(text: piece, box: toPixels(box))) }
                }
            }
            if words.isEmpty { words = [OcrWord(text: text, box: toPixels(obs.boundingBox))] }
            lines.append(OcrLine(words: words, text: text, noSpaces: cjk))
        }
        return OcrLayout(sortReadingOrder(lines))
    }

    static func preferredLanguages(for req: VNRecognizeTextRequest) -> [String] {
        let supported = (try? req.supportedRecognitionLanguages()) ?? ["en-US"]
        var langs: [String] = []
        let wanted = Locale.preferredLanguages.map { $0.lowercased() } + ["en-us", "ru-ru"]
        for w in wanted {
            if let s = supported.first(where: { $0.lowercased() == w || $0.lowercased().hasPrefix(w.prefix(2) + "-") || $0.lowercased() == w.prefix(2) }),
               !langs.contains(s) { langs.append(s) }
        }
        return langs.isEmpty ? ["en-US"] : langs
    }

    static func isCJK(_ u: Unicode.Scalar) -> Bool {
        (0x4E00...0x9FFF).contains(u.value) || (0x3040...0x30FF).contains(u.value) || (0x3400...0x4DBF).contains(u.value) || (0xAC00...0xD7AF).contains(u.value)
    }

    /// Word ranges: whitespace-separated pieces, or for CJK every character group split by script/punctuation.
    static func tokens(_ s: String, cjk: Bool) -> [(Range<String.Index>, String)] {
        var out: [(Range<String.Index>, String)] = []
        var start: String.Index?
        var i = s.startIndex
        while i < s.endIndex {
            let ch = s[i]
            let breaksHere = ch.isWhitespace || (cjk && (ch.unicodeScalars.first.map(isCJK) ?? false))
            if breaksHere {
                if let st = start { out.append((st..<i, String(s[st..<i]))); start = nil }
                if !ch.isWhitespace { let n = s.index(after: i); out.append((i..<n, String(ch))) }
            } else if start == nil { start = i }
            i = s.index(after: i)
        }
        if let st = start { out.append((st..<s.endIndex, String(s[st...]))) }
        return out
    }

    /// Lines top to bottom; lines that share a row go left to right.
    static func sortReadingOrder(_ lines: [OcrLine]) -> [OcrLine] {
        lines.sorted { a, b in
            let ra = OcrLayout.lineBox(a), rb = OcrLayout.lineBox(b)
            let overlap = min(ra.maxY, rb.maxY) - max(ra.minY, rb.minY)
            if overlap > min(ra.height, rb.height) * 0.5 { return ra.minX < rb.minX }
            return ra.minY < rb.minY
        }
    }

    public static func scanCodes(_ image: CGImage) -> [DetectedCode] {
        let req = VNDetectBarcodesRequest()
        try? VNImageRequestHandler(cgImage: image, options: [:]).perform([req])
        let w = CGFloat(image.width), h = CGFloat(image.height)
        var out: [DetectedCode] = []
        for o in req.results ?? [] {
            guard let text = o.payloadStringValue, !text.isEmpty else { continue }
            let r = o.boundingBox
            let box = CGRect(x: r.minX * w, y: (1 - r.maxY) * h, width: r.width * w, height: r.height * h)
            if out.contains(where: { $0.text == text && $0.box.intersects(box) }) { continue }
            out.append(DetectedCode(format: formatName(o.symbology), text: text, box: box.insetBy(dx: -3, dy: -3)))
        }
        return out
    }

    static func formatName(_ s: VNBarcodeSymbology) -> String {
        switch s {
        case .qr, .microQR: return Loc.t("Codes.QrCode")
        case .dataMatrix: return "Data Matrix"
        case .ean13: return "EAN-13"
        case .ean8: return "EAN-8"
        case .upce: return "UPC-E"
        case .code128: return "Code 128"
        case .code39, .code39Checksum, .code39FullASCII, .code39FullASCIIChecksum: return "Code 39"
        case .pdf417, .microPDF417: return "PDF417"
        case .aztec: return "Aztec"
        default: return s.rawValue.replacingOccurrences(of: "VNBarcodeSymbology", with: "")
        }
    }
}

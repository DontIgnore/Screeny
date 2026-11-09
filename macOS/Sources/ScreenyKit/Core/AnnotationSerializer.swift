import CoreGraphics
import Foundation

/// A document read from JSON: annotations in region coordinates, region size, beautify, skipped unknown items.
public struct AnnotationFile {
    public var annotations: [Annotation]
    public var width: Int
    public var height: Int
    public var beautify: BeautifyOptions?
    public var skipped = 0
}

/// `<stamp>.screeny.json`, byte-compatible in meaning with Annotations/AnnotationSerializer.cs (version 1).
public enum AnnotationSerializer {
    public static let currentVersion = 1

    public static func serialize(_ annotations: [Annotation], regionOrigin: CGPoint, width: Int, height: Int, beautify: BeautifyOptions?) -> String {
        let off = CGVector(dx: -regionOrigin.x, dy: -regionOrigin.y)
        var list: [[String: Any]] = []
        for a in annotations {
            guard let type = typeName(a) else { continue }
            var d: [String: Any] = ["type": type, "style": a.style.json()]
            switch a {
            case let l as LineAnnotation:
                let s = l.start + off, e = l.end + off
                d["x1"] = s.x; d["y1"] = s.y; d["x2"] = e.x; d["y2"] = e.y
            case let p as PenAnnotation:
                d["points"] = p.points.map { [($0 + off).x, ($0 + off).y] }
            case let b as BoxAnnotation:
                d["x"] = b.rect.minX + off.dx; d["y"] = b.rect.minY + off.dy; d["w"] = b.rect.width; d["h"] = b.rect.height
            case let t as TextAnnotation:
                let o = t.origin + off
                d["x"] = o.x; d["y"] = o.y; d["text"] = t.text
            case let n as NumberAnnotation:
                let c = n.center + off
                d["x"] = c.x; d["y"] = c.y; d["n"] = n.number
            default: break
            }
            list.append(d)
        }
        let root: [String: Any] = [
            "version": currentVersion, "size": ["w": width, "h": height],
            "beautify": beautify.map { $0.json() as Any } ?? NSNull(), "annotations": list,
        ]
        let data = try! JSONSerialization.data(withJSONObject: root, options: [.prettyPrinted, .sortedKeys])
        return String(decoding: data, as: UTF8.self)
    }

    static func typeName(_ a: Annotation) -> String? {
        switch a {
        case is ArrowAnnotation: return "arrow"
        case is LineAnnotation: return "line"
        case is HighlighterAnnotation: return "highlighter"
        case is PenAnnotation: return "pen"
        case is RectAnnotation: return "rect"
        case is EllipseAnnotation: return "ellipse"
        case is PixelateAnnotation: return "pixelate"
        case is BlurAnnotation: return "blur"
        case is TextAnnotation: return "text"
        case is NumberAnnotation: return "number"
        default: return nil
        }
    }

    public static func deserialize(_ json: String) throws -> AnnotationFile {
        guard let obj = try? JSONSerialization.jsonObject(with: Data(json.utf8)), let root = obj as? [String: Any] else {
            throw DocumentError.corrupt("Document root must be an object.")
        }
        guard let version = (try required(root, "version") as? NSNumber)?.intValue else { throw DocumentError.corrupt("Invalid version.") }
        if version < 1 { throw DocumentError.corrupt("Invalid annotation document version: \(version).") }
        if version > currentVersion { throw DocumentError.unsupportedVersion(version) }
        let size = try required(root, "size")
        let w = try int(size, "w"), h = try int(size, "h")
        if w <= 0 || h <= 0 { throw DocumentError.corrupt("Invalid document size.") }
        var beautify: BeautifyOptions?
        if let b = root["beautify"], !(b is NSNull) { beautify = try BeautifyOptions.from(json: b) }
        guard let items = try required(root, "annotations") as? [Any] else { throw DocumentError.corrupt("annotations is not an array.") }
        var list: [Annotation] = []
        var skipped = 0
        for item in items {
            let type = try required(item, "type") as? String
            if let a = try read(item, type) { list.append(a) } else { skipped += 1 }
        }
        return AnnotationFile(annotations: list, width: w, height: h, beautify: beautify, skipped: skipped)
    }

    private static func read(_ e: Any, _ type: String?) throws -> Annotation? {
        func style() throws -> AnnotationStyle { try AnnotationStyle.from(json: try required(e, "style")) }
        switch type {
        case "rect": return RectAnnotation(try rect(e), style: try style())
        case "ellipse": return EllipseAnnotation(try rect(e), style: try style())
        case "pixelate": return PixelateAnnotation(try rect(e), style: try style())
        case "blur": return BlurAnnotation(try rect(e), style: try style())
        case "line": return LineAnnotation(try point(e, "x1", "y1"), try point(e, "x2", "y2"), style: try style())
        case "arrow": return ArrowAnnotation(try point(e, "x1", "y1"), try point(e, "x2", "y2"), style: try style())
        case "pen", "highlighter":
            guard let arr = try required(e, "points") as? [Any] else { throw DocumentError.corrupt("points is not an array.") }
            var pts: [CGPoint] = []
            for p in arr {
                guard let pair = p as? [Any], pair.count == 2 else { throw DocumentError.corrupt("A stroke point must be a pair of numbers.") }
                pts.append(CGPoint(x: try finite(pair[0]), y: try finite(pair[1])))
            }
            guard let first = pts.first else { throw DocumentError.corrupt("Stroke has no points.") }
            let s = try style()
            let pen = type == "pen" ? PenAnnotation(first, style: s) : HighlighterAnnotation(first, style: s)
            for p in pts.dropFirst() { pen.addPoint(p) }
            return pen
        case "text":
            guard let t = try required(e, "text") as? String else { throw DocumentError.corrupt("text is not a string.") }
            return TextAnnotation(try point(e, "x", "y"), text: t, style: try style())
        case "number": return NumberAnnotation(try point(e, "x", "y"), number: try int(e, "n"), style: try style())
        default: return nil
        }
    }

    private static func required(_ e: Any, _ name: String) throws -> Any {
        guard let d = e as? [String: Any], let v = d[name], !(v is NSNull) else { throw DocumentError.corrupt("Missing required field \"\(name)\".") }
        return v
    }

    private static func finite(_ v: Any) throws -> Double {
        guard let n = v as? NSNumber, !(v is Bool), n.doubleValue.isFinite else { throw DocumentError.corrupt("Invalid number.") }
        return n.doubleValue
    }

    private static func int(_ e: Any, _ name: String) throws -> Int {
        let v = try finite(try required(e, name))
        guard v == v.rounded(), abs(v) < 1e9 else { throw DocumentError.corrupt("Invalid integer.") }
        return Int(v)
    }

    private static func number(_ e: Any, _ name: String) throws -> Double { try finite(try required(e, name)) }
    private static func point(_ e: Any, _ x: String, _ y: String) throws -> CGPoint { CGPoint(x: try number(e, x), y: try number(e, y)) }

    private static func rect(_ e: Any) throws -> CGRect {
        let w = try number(e, "w"), h = try number(e, "h")
        if w < 0 || h < 0 { throw DocumentError.corrupt("Negative shape size.") }
        return CGRect(x: try number(e, "x"), y: try number(e, "y"), width: w, height: h)
    }
}

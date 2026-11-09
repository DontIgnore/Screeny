import Foundation

public enum ArrowStyle: String, CaseIterable, Codable { case normal = "Normal", double = "Double", dashed = "Dashed", thick = "Thick" }
public enum TextAlign: String, CaseIterable, Codable { case left = "Left", center = "Center", right = "Right" }

/// Immutable annotation style (Annotations/AnnotationStyle.cs). `color` is the main color: shape stroke, line,
/// pen, text, badge. The other fields are used only by the shapes they belong to.
public struct AnnotationStyle: Equatable, Hashable, CustomStringConvertible {
    public var color: RGBA
    public var thickness: Double
    public var strokeEnabled = true
    public var fill: RGBA?
    public var arrow: ArrowStyle = .normal
    public var textBackground: RGBA?
    public var textAlign: TextAlign = .left

    public init(color: RGBA, thickness: Double) { self.color = color; self.thickness = thickness }

    public var strokeColor: RGBA? { strokeEnabled ? color : nil }

    /// Turning off the last visible part of a shape is ignored.
    public func withStroke(_ on: Bool) -> AnnotationStyle {
        if !on && fill == nil { return self }
        var s = self; s.strokeEnabled = on; return s
    }

    public func withFill(_ f: RGBA?) -> AnnotationStyle {
        if f == nil && !strokeEnabled { return self }
        var s = self; s.fill = f; return s
    }

    public var canDisableStroke: Bool { fill != nil }
    public var canDisableFill: Bool { strokeEnabled }

    public var description: String {
        "\(color)|\(thickness)|\(strokeEnabled)|\(fill?.argbHex ?? "-")|\(arrow)|\(textBackground?.argbHex ?? "-")|\(textAlign)"
    }

    // MARK: JSON (same shape as AnnotationStyleJsonConverter)

    func json() -> [String: Any] {
        [
            "color": color.argbHex, "thickness": thickness, "stroke": strokeEnabled,
            "fill": fill.map { $0.argbHex as Any } ?? NSNull(), "arrow": arrow.rawValue,
            "textBackground": textBackground.map { $0.argbHex as Any } ?? NSNull(), "textAlign": textAlign.rawValue,
        ]
    }

    static func from(json: Any?) throws -> AnnotationStyle {
        guard let d = json as? [String: Any] else { throw DocumentError.corrupt("Style must be an object.") }
        var color = RGBA.red, thickness = 3.0, stroke = true
        var fill: RGBA?, bg: RGBA?, arrow = ArrowStyle.normal, align = TextAlign.left
        for (k, v) in d {
            switch k.lowercased() {
            case "color": if let c = try readColor(v) { color = c }
            case "thickness":
                guard let t = (v as? NSNumber)?.doubleValue, t.isFinite, t > 0 else { throw DocumentError.corrupt("Invalid thickness.") }
                thickness = t
            case "stroke":
                guard let b = v as? Bool else { throw DocumentError.corrupt("Invalid stroke.") }
                stroke = b
            case "fill": fill = try readColor(v)
            case "textbackground": bg = try readColor(v)
            case "arrow":
                guard let s = v as? String, let a = ArrowStyle.allCases.first(where: { $0.rawValue.lowercased() == s.lowercased() })
                else { throw DocumentError.corrupt("Unknown arrow style.") }
                arrow = a
            case "textalign":
                guard let s = v as? String, let a = TextAlign.allCases.first(where: { $0.rawValue.lowercased() == s.lowercased() })
                else { throw DocumentError.corrupt("Unknown text alignment.") }
                align = a
            default: break
            }
        }
        var s = AnnotationStyle(color: color, thickness: thickness)
        s.strokeEnabled = stroke || fill == nil // never restore an invisible shape
        s.fill = fill; s.arrow = arrow; s.textBackground = bg; s.textAlign = align
        return s
    }

    private static func readColor(_ v: Any) throws -> RGBA? {
        if v is NSNull { return nil }
        guard let s = v as? String, let c = RGBA.parse(s) else { throw DocumentError.corrupt("Invalid color.") }
        return c
    }
}

public enum DocumentError: Error, Equatable, LocalizedError {
    case corrupt(String)
    case unsupportedVersion(Int)
    public var errorDescription: String? {
        switch self {
        case .corrupt(let m): return "Corrupt annotation document: \(m)"
        case .unsupportedVersion(let v): return "Unsupported annotation document version: \(v)."
        }
    }
}

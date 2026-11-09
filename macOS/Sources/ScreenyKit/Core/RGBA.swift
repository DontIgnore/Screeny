import AppKit
import CoreGraphics

/// 8-bit ARGB color, the same values as WPF `Color` on Windows. JSON form: "#AARRGGBB".
public struct RGBA: Equatable, Hashable, CustomStringConvertible {
    public var a, r, g, b: UInt8
    public init(_ r: UInt8, _ g: UInt8, _ b: UInt8, a: UInt8 = 255) { self.r = r; self.g = g; self.b = b; self.a = a }
    public init(hex rgb: Int) { self.init(UInt8((rgb >> 16) & 0xFF), UInt8((rgb >> 8) & 0xFF), UInt8(rgb & 0xFF)) }

    public static let white = RGBA(255, 255, 255)
    public static let black = RGBA(0, 0, 0)
    public static let red = RGBA(255, 0, 0)

    public var cgColor: CGColor {
        CGColor(srgbRed: CGFloat(r) / 255, green: CGFloat(g) / 255, blue: CGFloat(b) / 255, alpha: CGFloat(a) / 255)
    }
    public var nsColor: NSColor {
        NSColor(srgbRed: CGFloat(r) / 255, green: CGFloat(g) / 255, blue: CGFloat(b) / 255, alpha: CGFloat(a) / 255)
    }

    public var opaque: RGBA { RGBA(r, g, b) }
    /// "#RRGGBB" — magnifier, eyedropper clipboard.
    public var rgbHex: String { String(format: "#%02X%02X%02X", r, g, b) }
    /// "#AARRGGBB" — JSON.
    public var argbHex: String { String(format: "#%02X%02X%02X%02X", a, r, g, b) }
    public var description: String { argbHex }

    public func withOpacity(_ percent: Int) -> RGBA {
        RGBA(r, g, b, a: UInt8((Double(clamp(percent, 0, 100)) * 255 / 100).rounded()))
    }
    public var opacityPercent: Int { Int((Double(a) * 100 / 255).rounded()) }

    /// Parses "#RGB", "#RRGGBB", "#AARRGGBB" and a few named colors; nil if it isn't a color.
    public static func parse(_ text: String?) -> RGBA? {
        guard var s = text?.trimmingCharacters(in: .whitespaces), !s.isEmpty else { return nil }
        switch s.lowercased() {
        case "red": return RGBA(255, 0, 0)
        case "white": return .white
        case "black": return .black
        case "transparent": return RGBA(255, 255, 255, a: 0)
        default: break
        }
        guard s.hasPrefix("#") else { return nil }
        s.removeFirst()
        guard s.allSatisfy(\.isHexDigit), let v = UInt64(s, radix: 16) else { return nil }
        switch s.count {
        case 3:
            let r = UInt8((v >> 8) & 0xF) * 17, g = UInt8((v >> 4) & 0xF) * 17, b = UInt8(v & 0xF) * 17
            return RGBA(r, g, b)
        case 6: return RGBA(UInt8((v >> 16) & 0xFF), UInt8((v >> 8) & 0xFF), UInt8(v & 0xFF))
        case 8: return RGBA(UInt8((v >> 16) & 0xFF), UInt8((v >> 8) & 0xFF), UInt8(v & 0xFF), a: UInt8((v >> 24) & 0xFF))
        default: return nil
        }
    }
}

/// Brand palette (Themes/Theme.xaml, Dark.xaml, Light.xaml).
public enum Palette {
    public static let accent = RGBA(hex: 0x3078F6)
    public static let accentHover = RGBA(hex: 0x4A8BFF)
    public static let accentPressed = RGBA(hex: 0x2867D8)
    public static let danger = RGBA(hex: 0xFF3B30)
    /// Overlay HUD is always dark.
    public static let overlayPanel = RGBA(0x29, 0x25, 0x24, a: 0xF2)
    public static let overlayInk = RGBA(hex: 0xFFF7ED)
    public static let overlayInk70 = RGBA(0xFF, 0xF7, 0xED, a: 0xB3)
    public static let overlayHover = RGBA(0xFF, 0xF7, 0xED, a: 0x24)
    public static let overlayPressed = RGBA(0xFF, 0xF7, 0xED, a: 0x3D)
    public static let overlayDivider = RGBA(0xFF, 0xF7, 0xED, a: 0x33)
    public static let overlayShotRing = RGBA(255, 255, 255, a: 0x33)
    public static let overlayTooltip = RGBA(hex: 0x1C1917)

    /// Toolbar colors in order.
    public static let colors: [RGBA] = [
        RGBA(0xFF, 0x3B, 0x30), RGBA(0xFF, 0x95, 0x00), RGBA(0xFF, 0xCC, 0x00), RGBA(0x34, 0xC7, 0x59),
        RGBA(0x00, 0x7A, 0xFF), RGBA(0xAF, 0x52, 0xDE), .white, .black,
    ]
}

import CoreGraphics
import CoreImage
import Foundation

public enum BeautifyPreset: String, CaseIterable, Codable { case blue = "Blue", sunset = "Sunset", mint = "Mint", graphite = "Graphite", light = "Light", warm = "Warm" }
public enum BeautifyPadding: String, CaseIterable, Codable { case s = "S", m = "M", l = "L" }

/// Background frame: gradient, padding, rounded corners, soft shadow (Export/BeautifyOptions.cs).
public struct BeautifyOptions: Equatable, Hashable {
    public var preset: BeautifyPreset
    public var padding: BeautifyPadding
    public var radius: Int
    public var shadow: Bool

    public init(preset: BeautifyPreset, padding: BeautifyPadding, radius: Int, shadow: Bool) {
        self.preset = preset; self.padding = padding; self.radius = radius; self.shadow = shadow
    }

    public static let `default` = BeautifyOptions(preset: .blue, padding: .m, radius: 8, shadow: true)
    public static let radii = [0, 8, 16]

    public var paddingPx: Int { Self.paddingPixels(padding) }
    public static func paddingPixels(_ p: BeautifyPadding) -> Int { p == .s ? 24 : p == .l ? 80 : 48 }
    public var shadowBlur: Double { Double(paddingPx) / 3 }
    public var shadowOffset: Double { max(2, Double(paddingPx) / 6) }
    public static let shadowOpacity = 0.35

    public static func snapRadius(_ r: Int) -> Int { radii.min { (abs($0 - r), $0) < (abs($1 - r), $1) }! }

    public static func colors(_ p: BeautifyPreset) -> (RGBA, RGBA) {
        switch p {
        case .sunset: return (RGBA(hex: 0xFF7A59), RGBA(hex: 0xFFB547))
        case .mint: return (RGBA(hex: 0x2BD9A0), RGBA(hex: 0x3BA7F5))
        case .graphite: return (RGBA(hex: 0x2B2B33), RGBA(hex: 0x4A4A57))
        case .light: return (RGBA(hex: 0xF4F5F8), RGBA(hex: 0xDDE3EC))
        case .warm: return (RGBA(hex: 0xFFF7ED), RGBA(hex: 0xFAD7B5))
        case .blue: return (RGBA(hex: 0x3078F6), RGBA(hex: 0x7C5CFF))
        }
    }

    public static func name(_ p: BeautifyPreset) -> String { Loc.t("Beautify.\(p.rawValue)") }

    func json() -> [String: Any] { ["preset": preset.rawValue, "padding": padding.rawValue, "radius": radius, "shadow": shadow] }

    static func from(json: Any) throws -> BeautifyOptions {
        guard let d = json as? [String: Any] else { throw DocumentError.corrupt("beautify must be an object.") }
        guard let ps = d["preset"] as? String, let preset = BeautifyPreset.allCases.first(where: { $0.rawValue.lowercased() == ps.lowercased() }),
              let pd = d["padding"] as? String, let padding = BeautifyPadding.allCases.first(where: { $0.rawValue.lowercased() == pd.lowercased() }),
              let r = (d["radius"] as? NSNumber)?.intValue, let sh = d["shadow"] as? Bool
        else { throw DocumentError.corrupt("Invalid beautify options.") }
        return BeautifyOptions(preset: preset, padding: padding, radius: snapRadius(r), shadow: sh)
    }
}

public enum BeautifyComposer {
    public static func size(_ w: Int, _ h: Int, _ o: BeautifyOptions) -> (Int, Int) { (w + 2 * o.paddingPx, h + 2 * o.paddingPx) }

    /// Gradient over the whole canvas, the picture inset by padding with rounded corners, a soft shadow.
    /// The result is fully opaque.
    public static func compose(_ content: CGImage, _ o: BeautifyOptions) -> CGImage {
        let w = content.width, h = content.height, p = o.paddingPx
        let W = w + 2 * p, H = h + 2 * p
        let ctx = CGContext(data: nil, width: W, height: H, bitsPerComponent: 8, bytesPerRow: 0, space: PixelImage.colorSpace,
                            bitmapInfo: PixelImage.bitmapInfo)!
        // Work in y-down coordinates like the rest of the app.
        ctx.translateBy(x: 0, y: CGFloat(H)); ctx.scaleBy(x: 1, y: -1)
        let (from, to) = BeautifyOptions.colors(o.preset)
        ctx.setFillColor(from.cgColor)
        ctx.fill(CGRect(x: 0, y: 0, width: W, height: H))
        let grad = CGGradient(colorsSpace: PixelImage.colorSpace, colors: [from.cgColor, to.cgColor] as CFArray, locations: [0, 1])!
        // WPF's relative (0,0)→(1,1) gradient: perpendicular lines follow the canvas diagonal.
        ctx.saveGState()
        ctx.scaleBy(x: CGFloat(W), y: CGFloat(H))
        ctx.drawLinearGradient(grad, start: .zero, end: CGPoint(x: 1, y: 1), options: [.drawsBeforeStartLocation, .drawsAfterEndLocation])
        ctx.restoreGState()

        let inner = CGRect(x: p, y: p, width: w, height: h)
        let r = min(Double(o.radius), Double(min(w, h)) / 2)
        let clip = CGPath(roundedRect: inner, cornerWidth: r, cornerHeight: r, transform: nil)
        if o.shadow {
            ctx.saveGState()
            // y-down context: positive offset in device space moves down after our flip → use negative height direction.
            ctx.setShadow(offset: CGSize(width: 0, height: -o.shadowOffset), blur: o.shadowBlur * 2,
                          color: CGColor(gray: 0, alpha: BeautifyOptions.shadowOpacity))
            ctx.addPath(clip)
            ctx.setFillColor(CGColor(gray: 0, alpha: 1))
            ctx.fillPath()
            ctx.restoreGState()
        }
        ctx.saveGState()
        ctx.addPath(clip); ctx.clip()
        ctx.interpolationQuality = .none
        ctx.translateBy(x: inner.minX, y: inner.maxY); ctx.scaleBy(x: 1, y: -1)
        ctx.draw(content, in: CGRect(x: 0, y: 0, width: w, height: h))
        ctx.restoreGState()
        return ctx.makeImage()!
    }
}

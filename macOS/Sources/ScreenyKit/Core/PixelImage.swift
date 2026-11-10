import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

/// A screenshot in memory: premultiplied RGBA8 pixels (sRGB, top row first) plus a CGImage over the same pixels.
/// Effects (pixelate, blur), the eyedropper and the magnifier read the buffer directly.
public final class PixelImage {
    public let width: Int
    public let height: Int
    public let pixels: [UInt8]
    public let cgImage: CGImage
    /// Pixels per point of the display it came from (2 on Retina). Editors show it at width / scale points.
    public let scale: CGFloat

    public static let colorSpace = CGColorSpace(name: CGColorSpace.sRGB)!
    public static let bitmapInfo = CGImageAlphaInfo.premultipliedLast.rawValue | CGBitmapInfo.byteOrder32Big.rawValue

    public init(pixels: [UInt8], width: Int, height: Int, scale: CGFloat = 1) {
        precondition(pixels.count == width * height * 4)
        self.width = width; self.height = height; self.pixels = pixels; self.scale = scale
        let provider = CGDataProvider(data: Data(pixels) as CFData)!
        cgImage = CGImage(width: width, height: height, bitsPerComponent: 8, bitsPerPixel: 32, bytesPerRow: width * 4,
                          space: Self.colorSpace, bitmapInfo: CGBitmapInfo(rawValue: Self.bitmapInfo), provider: provider,
                          decode: nil, shouldInterpolate: false, intent: .defaultIntent)!
    }

    /// Redraws any CGImage into our pixel format (sRGB, premultiplied RGBA).
    public convenience init(cgImage source: CGImage, scale: CGFloat = 1) {
        let w = source.width, h = source.height
        var px = [UInt8](repeating: 0, count: w * h * 4)
        px.withUnsafeMutableBytes { buf in
            let ctx = CGContext(data: buf.baseAddress, width: w, height: h, bitsPerComponent: 8, bytesPerRow: w * 4,
                                space: Self.colorSpace, bitmapInfo: Self.bitmapInfo)!
            ctx.interpolationQuality = .none
            ctx.draw(source, in: CGRect(x: 0, y: 0, width: w, height: h))
        }
        self.init(pixels: px, width: w, height: h, scale: scale)
    }

    public var bounds: PixelRect { PixelRect(x: 0, y: 0, width: width, height: height) }

    /// Un-premultiplied color of one pixel (eyedropper, magnifier).
    public func color(at p: PixelPoint) -> RGBA {
        let x = clamp(p.x, 0, width - 1), y = clamp(p.y, 0, height - 1)
        let i = (y * width + x) * 4
        let a = pixels[i + 3]
        if a == 0 { return RGBA(0, 0, 0, a: 0) }
        func un(_ v: UInt8) -> UInt8 { a == 255 ? v : UInt8(min(255, (Int(v) * 255 + Int(a) / 2) / Int(a))) }
        return RGBA(un(pixels[i]), un(pixels[i + 1]), un(pixels[i + 2]), a: a)
    }

    /// Copy of a sub-rectangle's pixels (must lie inside the image).
    public func copyPixels(_ r: PixelRect) -> [UInt8] {
        var out = [UInt8](repeating: 0, count: r.width * r.height * 4)
        pixels.withUnsafeBufferPointer { src in
            out.withUnsafeMutableBufferPointer { dst in
                for row in 0..<r.height {
                    let s = ((r.y + row) * width + r.x) * 4
                    let d = row * r.width * 4
                    for k in 0..<(r.width * 4) { dst[d + k] = src[s + k] }
                }
            }
        }
        return out
    }

    public func cropped(_ r: PixelRect) -> PixelImage {
        let c = r.clamped(to: bounds)
        return PixelImage(pixels: copyPixels(c), width: c.width, height: c.height, scale: scale)
    }

    // MARK: Files

    public static func load(url: URL) -> PixelImage? {
        guard let src = CGImageSourceCreateWithURL(url as CFURL, nil),
              let img = CGImageSourceCreateImageAtIndex(src, 0, nil) else { return nil }
        return PixelImage(cgImage: img)
    }

    public func pngData() -> Data? { Self.encode(cgImage, type: .png) }

    public static func encode(_ image: CGImage, type: UTType, quality: Double = 0.92) -> Data? {
        let data = NSMutableData()
        guard let dest = CGImageDestinationCreateWithData(data, type.identifier as CFString, 1, nil) else { return nil }
        var props: [CFString: Any] = [:]
        if type == .jpeg { props[kCGImageDestinationLossyCompressionQuality] = quality }
        var img = image
        if type == .jpeg, let flat = flatten(image) { img = flat } // JPEG has no alpha: compose over white
        CGImageDestinationAddImage(dest, img, props as CFDictionary)
        return CGImageDestinationFinalize(dest) ? data as Data : nil
    }

    static func flatten(_ image: CGImage) -> CGImage? {
        let ctx = CGContext(data: nil, width: image.width, height: image.height, bitsPerComponent: 8, bytesPerRow: 0,
                            space: colorSpace, bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue)
        ctx?.setFillColor(CGColor(gray: 1, alpha: 1))
        ctx?.fill(CGRect(x: 0, y: 0, width: image.width, height: image.height))
        ctx?.draw(image, in: CGRect(x: 0, y: 0, width: image.width, height: image.height))
        return ctx?.makeImage()
    }
}

/// In-place operations on an RGBA8 buffer (stride = width*4), a port of Annotations/PixelOps.cs.
public enum PixelOps {
    public static func pixelate(_ px: inout [UInt8], width: Int, height: Int, blockSize: Int) {
        let bs = max(1, blockSize)
        px.withUnsafeMutableBufferPointer { p in
            var by = 0
            while by < height {
                var bx = 0
                while bx < width {
                    let bw = min(bs, width - bx), bh = min(bs, height - by)
                    var s0 = 0, s1 = 0, s2 = 0, s3 = 0
                    for y in by..<(by + bh) {
                        for x in bx..<(bx + bw) {
                            let i = (y * width + x) * 4
                            s0 += Int(p[i]); s1 += Int(p[i + 1]); s2 += Int(p[i + 2]); s3 += Int(p[i + 3])
                        }
                    }
                    let n = Double(bw * bh)
                    let a0 = UInt8((Double(s0) / n).rounded()), a1 = UInt8((Double(s1) / n).rounded())
                    let a2 = UInt8((Double(s2) / n).rounded()), a3 = UInt8((Double(s3) / n).rounded())
                    for y in by..<(by + bh) {
                        for x in bx..<(bx + bw) {
                            let i = (y * width + x) * 4
                            p[i] = a0; p[i + 1] = a1; p[i + 2] = a2; p[i + 3] = a3
                        }
                    }
                    bx += bs
                }
                by += bs
            }
        }
    }

    /// Horizontal + vertical box average with window 2r+1, clamped edges, O(w·h).
    public static func boxBlur(_ px: inout [UInt8], width w: Int, height h: Int, radius r: Int) {
        guard r > 0, w > 0, h > 0 else { return }
        var tmp = [UInt8](repeating: 0, count: w * h * 4)
        let n = 2 * r + 1
        px.withUnsafeMutableBufferPointer { src in
            tmp.withUnsafeMutableBufferPointer { dst in
                // Horizontal: src → dst
                let last = w - 1
                for y in 0..<h {
                    let row = y * w * 4
                    var s = (0, 0, 0, 0)
                    for k in -r...r {
                        let i = row + clamp(k, 0, last) * 4
                        s.0 += Int(src[i]); s.1 += Int(src[i + 1]); s.2 += Int(src[i + 2]); s.3 += Int(src[i + 3])
                    }
                    for x in 0..<w {
                        let o = row + x * 4
                        dst[o] = UInt8(s.0 / n); dst[o + 1] = UInt8(s.1 / n); dst[o + 2] = UInt8(s.2 / n); dst[o + 3] = UInt8(s.3 / n)
                        let add = row + min(x + r + 1, last) * 4, sub = row + max(x - r, 0) * 4
                        s.0 += Int(src[add]) - Int(src[sub]); s.1 += Int(src[add + 1]) - Int(src[sub + 1])
                        s.2 += Int(src[add + 2]) - Int(src[sub + 2]); s.3 += Int(src[add + 3]) - Int(src[sub + 3])
                    }
                }
                // Vertical: dst → src
                let lastY = h - 1, rowLen = w * 4
                var sums = [Int](repeating: 0, count: rowLen)
                for k in -r...r {
                    let row = clamp(k, 0, lastY) * rowLen
                    for c in 0..<rowLen { sums[c] += Int(dst[row + c]) }
                }
                for y in 0..<h {
                    let o = y * rowLen
                    for c in 0..<rowLen { src[o + c] = UInt8(sums[c] / n) }
                    let add = min(y + r + 1, lastY) * rowLen, sub = max(y - r, 0) * rowLen
                    for c in 0..<rowLen { sums[c] += Int(dst[add + c]) - Int(dst[sub + c]) }
                }
            }
        }
    }
}

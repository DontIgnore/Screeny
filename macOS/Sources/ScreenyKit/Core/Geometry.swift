import CoreGraphics
import Foundation

// Points and rects are in screenshot pixels with y pointing down, like the Windows build.

public extension CGPoint {
    static func + (a: CGPoint, b: CGVector) -> CGPoint { CGPoint(x: a.x + b.dx, y: a.y + b.dy) }
    static func - (a: CGPoint, b: CGVector) -> CGPoint { CGPoint(x: a.x - b.dx, y: a.y - b.dy) }
    static func - (a: CGPoint, b: CGPoint) -> CGVector { CGVector(dx: a.x - b.x, dy: a.y - b.y) }
    static func += (a: inout CGPoint, b: CGVector) { a = a + b }
}

public extension CGVector {
    static func + (a: CGVector, b: CGVector) -> CGVector { CGVector(dx: a.dx + b.dx, dy: a.dy + b.dy) }
    static func - (a: CGVector, b: CGVector) -> CGVector { CGVector(dx: a.dx - b.dx, dy: a.dy - b.dy) }
    static func * (a: CGVector, k: CGFloat) -> CGVector { CGVector(dx: a.dx * k, dy: a.dy * k) }
    static func / (a: CGVector, k: CGFloat) -> CGVector { CGVector(dx: a.dx / k, dy: a.dy / k) }
    static prefix func - (a: CGVector) -> CGVector { CGVector(dx: -a.dx, dy: -a.dy) }
    var length: CGFloat { (dx * dx + dy * dy).squareRoot() }
    var lengthSquared: CGFloat { dx * dx + dy * dy }
}

public extension CGRect {
    /// Rect spanning two corners in any order (WPF `new Rect(Point, Point)`).
    init(corner a: CGPoint, _ b: CGPoint) {
        self.init(x: min(a.x, b.x), y: min(a.y, b.y), width: abs(b.x - a.x), height: abs(b.y - a.y))
    }

    func inflated(_ d: CGFloat) -> CGRect {
        if isNull { return self }
        if d < 0 && (width < -2 * d || height < -2 * d) { return .null }
        return insetBy(dx: -d, dy: -d)
    }

    func offset(_ v: CGVector) -> CGRect { offsetBy(dx: v.dx, dy: v.dy) }

    var center: CGPoint { CGPoint(x: midX, y: midY) }

    /// Rect with zero size counts as "has area" for WPF-like containment of points on edges.
    func containsInclusive(_ p: CGPoint) -> Bool {
        !isNull && p.x >= minX && p.x <= maxX && p.y >= minY && p.y <= maxY
    }

    func union(point p: CGPoint) -> CGRect {
        isNull ? CGRect(x: p.x, y: p.y, width: 0, height: 0) : union(CGRect(x: p.x, y: p.y, width: 0, height: 0))
    }
}

/// Integer rect in screenshot pixels.
public struct PixelRect: Equatable, Hashable, Codable, CustomStringConvertible {
    public var x, y, width, height: Int
    public init(x: Int, y: Int, width: Int, height: Int) { self.x = x; self.y = y; self.width = width; self.height = height }
    public static let empty = PixelRect(x: 0, y: 0, width: 0, height: 0)
    public var isEmpty: Bool { width <= 0 || height <= 0 }
    public var right: Int { x + width }
    public var bottom: Int { y + height }
    public var cgRect: CGRect { CGRect(x: x, y: y, width: width, height: height) }
    public var description: String { "\(x),\(y) \(width)×\(height)" }

    public static func fromCorners(_ x1: Int, _ y1: Int, _ x2: Int, _ y2: Int) -> PixelRect {
        PixelRect(x: x1, y: y1, width: x2 - x1, height: y2 - y1)
    }

    public func contains(_ p: PixelPoint) -> Bool { p.x >= x && p.x < right && p.y >= y && p.y < bottom }

    public func clamped(to b: PixelRect) -> PixelRect {
        let x1 = max(x, b.x), y1 = max(y, b.y), x2 = min(right, b.right), y2 = min(bottom, b.bottom)
        return x2 > x1 && y2 > y1 ? .fromCorners(x1, y1, x2, y2) : .empty
    }

    public func offset(_ dx: Int, _ dy: Int) -> PixelRect { PixelRect(x: x + dx, y: y + dy, width: width, height: height) }

    /// Rounded outward to whole pixels.
    public init(enclosing r: CGRect) {
        let x1 = Int(floor(r.minX)), y1 = Int(floor(r.minY)), x2 = Int(ceil(r.maxX)), y2 = Int(ceil(r.maxY))
        self.init(x: x1, y: y1, width: x2 - x1, height: y2 - y1)
    }
}

public struct PixelPoint: Equatable, Hashable {
    public var x, y: Int
    public init(x: Int, y: Int) { self.x = x; self.y = y }
    public var cgPoint: CGPoint { CGPoint(x: x, y: y) }
}

public func clamp<T: Comparable>(_ v: T, _ lo: T, _ hi: T) -> T { max(lo, min(v, hi)) }

/// Clamp where the lower bound wins when the range is inverted (HudLayout.ClampInto).
public func clampInto(_ v: CGFloat, _ lo: CGFloat, _ hi: CGFloat) -> CGFloat { max(lo, min(v, hi)) }

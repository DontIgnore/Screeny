import XCTest
@testable import ScreenyKit

final class PinGeometryTests: XCTestCase {
    func testFitNeverUpscales() {
        XCTAssertEqual(PinGeometry.fitZoom(CGSize(width: 100, height: 50), in: CGSize(width: 1000, height: 800)), 1)
    }

    func testFitKeepsAspectWithin80Percent() {
        let z = PinGeometry.fitZoom(CGSize(width: 2000, height: 500), in: CGSize(width: 1000, height: 1000))
        XCTAssertEqual(z, 0.4, accuracy: 1e-9) // 2000 → 800 wide
        let t = PinGeometry.fitZoom(CGSize(width: 500, height: 2000), in: CGSize(width: 1000, height: 1000))
        XCTAssertEqual(t, 0.4, accuracy: 1e-9)
    }

    func testClampZoom() {
        let small = CGSize(width: 100, height: 100), desk = CGSize(width: 3000, height: 2000)
        XCTAssertEqual(PinGeometry.clampZoom(0.05, size: small, desktop: desk), 0.2)
        XCTAssertEqual(PinGeometry.clampZoom(9, size: small, desktop: desk), 5)
        XCTAssertEqual(PinGeometry.clampZoom(1.5, size: small, desktop: desk), 1.5)
        // Large picture: never bigger than the desktop.
        XCTAssertEqual(PinGeometry.clampZoom(4, size: CGSize(width: 1000, height: 1000), desktop: desk), 2)
        // Huge picture: the lower bound still wins.
        XCTAssertEqual(PinGeometry.clampZoom(1, size: CGSize(width: 100_000, height: 10), desktop: desk), 0.2)
    }

    func testZoomKeepsAnchorInPlace() {
        let old = CGRect(x: 100, y: 100, width: 200, height: 100)
        let anchor = CGPoint(x: 150, y: 175) // 25 %, 75 %
        let new = PinGeometry.zoomedFrame(old, to: CGSize(width: 400, height: 200), anchor: anchor)
        XCTAssertEqual(new, CGRect(x: 50, y: 25, width: 400, height: 200))
        XCTAssertEqual(new.minX + 0.25 * new.width, anchor.x)
        XCTAssertEqual(new.minY + 0.75 * new.height, anchor.y)
    }

    func testZoomWithoutAnchorKeepsCenter() {
        let new = PinGeometry.zoomedFrame(CGRect(x: 0, y: 0, width: 100, height: 100), to: CGSize(width: 50, height: 50), anchor: nil)
        XCTAssertEqual(new, CGRect(x: 25, y: 25, width: 50, height: 50))
    }

    func testOpacityClamp() {
        XCTAssertEqual(PinGeometry.clampOpacity(0.05), 0.2)
        XCTAssertEqual(PinGeometry.clampOpacity(1.3), 1)
        XCTAssertEqual(PinGeometry.clampOpacity(0.5), 0.5)
    }
}

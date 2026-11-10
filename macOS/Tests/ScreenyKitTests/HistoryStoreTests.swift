import XCTest
@testable import ScreenyKit

final class HistoryStoreTests: XCTestCase {
    func testConcurrentSavesKeepEverySidecar() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = HistoryStore(folder: dir, limit: 100)
        let time = Date()
        let errors = NSLock(); var failed: [Error] = []
        // Same millisecond and two stores for one folder, like the capture queue and the editor saving at once.
        DispatchQueue.concurrentPerform(iterations: 40) { i in
            let s = i % 2 == 0 ? store : HistoryStore(folder: dir, limit: 100)
            do { try s.save(png: Data(repeating: UInt8(i), count: 4096), json: "{\"i\":\(i)}", originalPng: Data(repeating: 1, count: 2048), at: time) }
            catch { errors.lock(); failed.append(error); errors.unlock() }
        }
        XCTAssertTrue(failed.isEmpty, "\(failed)")
        let items = store.list()
        XCTAssertEqual(items.count, 40)
        XCTAssertTrue(items.allSatisfy { $0.jsonURL != nil && $0.originalURL != nil })
        let jsons = Set(items.compactMap { $0.jsonURL.flatMap { try? String(contentsOf: $0, encoding: .utf8) } })
        XCTAssertEqual(jsons.count, 40)
        let leftovers = try FileManager.default.contentsOfDirectory(atPath: dir.path).filter { $0.hasSuffix(".tmp") }
        XCTAssertEqual(leftovers, [])
    }

    func testFrameOnlyShotKeepsOriginalWhenSettingIsOn() {
        let shot = PixelImage(pixels: [UInt8](repeating: 200, count: 40 * 20 * 4), width: 40, height: 20)
        let region = PixelRect(x: 2, y: 3, width: 10, height: 8)
        let kept = OverlayView.originalForHistory(keep: true, shot: shot, region: region)
        XCTAssertEqual(kept?.width, 10)
        XCTAssertEqual(kept?.height, 8)
        XCTAssertNil(OverlayView.originalForHistory(keep: false, shot: shot, region: region))
    }
}

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

final class MacReleasePickTests: XCTestCase {
    func testPicksNewestPublishedReleaseWithMacZip() {
        func rel(_ tag: String, _ assets: [String], draft: Bool = false) -> [String: Any] {
            ["tag_name": tag, "draft": draft, "html_url": "https://example.com/\(tag)",
             "assets": assets.map { ["name": $0, "browser_download_url": "https://example.com/\($0)"] }]
        }
        let list = [rel("v0.3.2-mac.1", ["Screeny-mac-0.3.2.zip"], draft: true),
                    rel("v0.3.0-alpha.2", ["Screeny-win-Setup.exe", "releases.win.json"]),
                    rel("v0.3.1-mac.1", ["Screeny-mac-0.3.1.zip"]),
                    rel("v0.3.0-mac.1", ["Screeny-mac-0.3.0.zip"])]
        let r = UpdateChecker.macRelease(list)
        XCTAssertEqual(r?.version, "0.3.1-mac.1")
        XCTAssertEqual(r?.macAssetURL?.lastPathComponent, "Screeny-mac-0.3.1.zip")
        XCTAssertEqual(UpdateChecker.compareVersions(r!.version, "0.3.0"), .orderedDescending)
        XCTAssertNotEqual(UpdateChecker.compareVersions(r!.version, "0.3.1"), .orderedDescending)
        XCTAssertNil(UpdateChecker.macRelease([rel("v0.2.3", ["Screeny-win-Setup.exe"])]))
    }
}

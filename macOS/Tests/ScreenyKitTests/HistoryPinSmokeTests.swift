import AppKit
import XCTest
@testable import ScreenyKit

private final class FakeServices: AppServices {
    let settings = AppSettings()
    let history: HistoryStore
    let historyChanged = Notification.Name("test.historyChanged")
    var copied = 0, edited: [HistoryItem] = []
    init(folder: URL) { history = HistoryStore(folder: folder, limit: 10) }
    func saveSettings() -> Bool { true }
    func startCapture() {}
    func showHistory() {}
    func showSettings() {}
    func showSetupWizard() {}
    func showFeedback(bug: Bool) {}
    func checkForUpdates(interactive: Bool) {}
    func openEditor(_ item: HistoryItem) { edited.append(item) }
    func pin(_ image: CGImage, scale: CGFloat) {}
    func copyImage(_ image: CGImage, scale: CGFloat) -> Bool { copied += 1; return true }
    func toast(_ text: String) {}
    var hotkeyText: String? { "⌘⇧2" }
}

final class HistoryPinSmokeTests: XCTestCase {
    func testHistoryWindowListsCardsAndHandlesKeys() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: dir) }
        let px = PixelImage(pixels: [UInt8](repeating: 200, count: 40 * 20 * 4), width: 40, height: 20)
        let s = FakeServices(folder: dir)
        try s.history.save(png: px.pngData()!, at: Date(timeIntervalSince1970: 1_700_000_000))
        try s.history.save(png: px.pngData()!, at: Date(timeIntervalSince1970: 1_700_000_100))
        let c = HistoryWindowController(services: s)
        c.show()
        let grid = try XCTUnwrap(c.window?.contentView?.descendants.compactMap { $0 as? HistoryGridView }.first)
        XCTAssertEqual(grid.cards.count, 2)
        c.copy(grid.cards[0])
        XCTAssertEqual(s.copied, 1)
        let e = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0, windowNumber: 0, context: nil,
                                 characters: "e", charactersIgnoringModifiers: "e", isARepeat: false, keyCode: 14)!
        grid.keyDown(with: e)
        XCTAssertEqual(s.edited.count, 1)
        c.close()
    }

    func testFrameOnlyChangeCountsAsUnsaved() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        defer { try? FileManager.default.removeItem(at: dir) }
        let px = PixelImage(pixels: [UInt8](repeating: 200, count: 40 * 20 * 4), width: 40, height: 20)
        let s = FakeServices(folder: dir)
        let url = try s.history.save(png: px.pngData()!)
        let item = try XCTUnwrap(s.history.list().first { $0.url == url })
        let c = try XCTUnwrap(EditorWindowController(item: item, services: s))
        XCTAssertFalse(c.dirty)
        c.canvas.editor.setBeautify(true)
        XCTAssertTrue(c.dirty, "turning the frame on must ask before closing")
        c.canvas.editor.setBeautify(false)
        XCTAssertFalse(c.dirty)
        c.close()
    }

    func testPinOpensFittedAndRegisters() {
        let img = PixelImage(pixels: [UInt8](repeating: 200, count: 100 * 60 * 4), width: 100, height: 60).cgImage
        let s = FakeServices(folder: FileManager.default.temporaryDirectory)
        let p = PinWindow.open(image: img, scale: 2, services: s, near: nil)
        XCTAssertEqual(p.frame.size, CGSize(width: 50, height: 30))
        XCTAssertTrue(PinWindow.windowNumbers.contains(p.windowNumber))
        p.zoom(to: 2, anchor: nil)
        XCTAssertEqual(p.frame.size, CGSize(width: 100, height: 60))
        p.close()
        XCTAssertTrue(PinWindow.all.isEmpty)
    }
}

private extension NSView {
    var descendants: [NSView] { subviews + subviews.flatMap(\.descendants) }
}

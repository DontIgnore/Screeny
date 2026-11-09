import XCTest
import AppKit
@testable import ScreenyKit

final class CoreTests: XCTestCase {
    private func style(_ c: RGBA = Palette.colors[0], _ t: Double = 3) -> AnnotationStyle { AnnotationStyle(color: c, thickness: t) }

    func testSerializerRoundTripAllTypesAndRegionOffset() throws {
        var s = style(); s.fill = RGBA(255, 0, 0, a: 102); s.arrow = .dashed; s.textBackground = .black; s.textAlign = .center
        let pen = PenAnnotation(CGPoint(x: 110, y: 120), style: style()); pen.addPoint(CGPoint(x: 130, y: 140))
        let hl = HighlighterAnnotation(CGPoint(x: 100, y: 100), style: style()); hl.addPoint(CGPoint(x: 150, y: 100))
        let items: [Annotation] = [
            RectAnnotation(CGRect(x: 110, y: 120, width: 30, height: 40), style: s),
            EllipseAnnotation(CGRect(x: 100, y: 100, width: 10, height: 10), style: style()),
            LineAnnotation(CGPoint(x: 100, y: 100), CGPoint(x: 150, y: 160), style: style()),
            ArrowAnnotation(CGPoint(x: 100, y: 100), CGPoint(x: 200, y: 100), style: s),
            pen, hl,
            TextAnnotation(CGPoint(x: 120, y: 130), text: "Привет\nworld", style: s),
            NumberAnnotation(CGPoint(x: 140, y: 140), number: 7, style: style()),
            PixelateAnnotation(CGRect(x: 100, y: 100, width: 20, height: 20), style: style()),
            BlurAnnotation(CGRect(x: 105, y: 105, width: 20, height: 20), style: style()),
        ]
        let json = AnnotationSerializer.serialize(items, regionOrigin: CGPoint(x: 100, y: 100), width: 300, height: 200, beautify: .default)
        let file = try AnnotationSerializer.deserialize(json)
        XCTAssertEqual(file.width, 300); XCTAssertEqual(file.height, 200); XCTAssertEqual(file.beautify, .default)
        XCTAssertEqual(file.annotations.count, items.count)
        XCTAssertEqual(AnnotationSerializer.typeName(file.annotations[3]), "arrow")
        XCTAssertEqual(AnnotationSerializer.typeName(file.annotations[5]), "highlighter")
        XCTAssertEqual((file.annotations[0] as! RectAnnotation).rect, CGRect(x: 10, y: 20, width: 30, height: 40))
        XCTAssertEqual(file.annotations[0].style, s)
        XCTAssertEqual((file.annotations[6] as! TextAnnotation).text, "Привет\nworld")
        XCTAssertEqual((file.annotations[7] as! NumberAnnotation).number, 7)
        XCTAssertEqual((file.annotations[4] as! PenAnnotation).points, [CGPoint(x: 10, y: 20), CGPoint(x: 30, y: 40)])
    }

    /// A document written by the Windows build (AnnotationSerializer.cs shape) reads on the Mac.
    func testReadsWindowsDocument() throws {
        let json = """
        { "version": 1, "size": { "w": 900, "h": 560 }, "beautify": { "preset": "Sunset", "padding": "L", "radius": 16, "shadow": true },
          "annotations": [
            { "type": "arrow", "style": { "color": "#FFFF3B30", "thickness": 3, "stroke": true, "fill": null, "arrow": "Thick", "textBackground": null, "textAlign": "Left" },
              "x1": 1.5, "y1": 2, "x2": 300, "y2": 200 },
            { "type": "future-shape", "style": { "color": "#FF000000", "thickness": 1 } },
            { "type": "rect", "style": { "color": "#FF007AFF", "thickness": 2, "stroke": false, "fill": "#66FF3B30" }, "x": 1, "y": 2, "w": 3, "h": 4 }
          ] }
        """
        let f = try AnnotationSerializer.deserialize(json)
        XCTAssertEqual(f.skipped, 1)
        XCTAssertEqual(f.beautify, BeautifyOptions(preset: .sunset, padding: .l, radius: 16, shadow: true))
        XCTAssertEqual(f.annotations[0].style.arrow, .thick)
        XCTAssertFalse(f.annotations[1].style.strokeEnabled)
        XCTAssertEqual(f.annotations[1].style.fill, RGBA(0xFF, 0x3B, 0x30, a: 0x66))
    }

    func testRejectsNewerAndBrokenDocuments() {
        XCTAssertThrowsError(try AnnotationSerializer.deserialize(#"{"version":2,"size":{"w":1,"h":1},"annotations":[]}"#)) {
            XCTAssertEqual($0 as? DocumentError, .unsupportedVersion(2))
        }
        XCTAssertThrowsError(try AnnotationSerializer.deserialize("{"))
        XCTAssertThrowsError(try AnnotationSerializer.deserialize(#"{"version":1,"size":{"w":0,"h":1},"annotations":[]}"#))
        XCTAssertThrowsError(try AnnotationSerializer.deserialize(#"{"version":1,"size":{"w":1,"h":1},"annotations":[{"type":"rect","style":{"color":"nope"},"x":0,"y":0,"w":1,"h":1}]}"#))
    }

    func testUndoRedoAndRestyle() {
        let doc = AnnotationDocument()
        let r = RectAnnotation(CGRect(x: 0, y: 0, width: 10, height: 10), style: style())
        doc.add(r)
        doc.move(r, CGVector(dx: 5, dy: 0))
        var s = style(); s.color = .black
        doc.restyle(r, s)
        XCTAssertEqual(r.rect.minX, 5); XCTAssertEqual(r.style.color, .black)
        doc.undo.undo(); XCTAssertEqual(r.style.color, Palette.colors[0])
        doc.undo.undo(); XCTAssertEqual(r.rect.minX, 0)
        doc.undo.undo(); XCTAssertTrue(doc.items.isEmpty)
        doc.undo.redo(); XCTAssertEqual(doc.items.count, 1)
        doc.removeAll(); XCTAssertTrue(doc.items.isEmpty)
        doc.undo.undo(); XCTAssertEqual(doc.items.count, 1)
        XCTAssertEqual(doc.nextNumber(), 1)
    }

    func testStyleCannotBecomeInvisible() {
        let s = style()
        XCTAssertEqual(s.withStroke(false), s) // no fill: stroke stays
        var f = s.withFill(.red); f = f.withStroke(false)
        XCTAssertFalse(f.strokeEnabled)
        XCTAssertEqual(f.withFill(nil), f) // no stroke: fill stays
    }

    func testHistoryStoreNamesSidecarsAndPrune() throws {
        let dir = FileManager.default.temporaryDirectory.appendingPathComponent("screeny-test-\(UUID())")
        defer { try? FileManager.default.removeItem(at: dir) }
        let store = HistoryStore(folder: dir, limit: 2)
        let t = Date(timeIntervalSince1970: 1_700_000_000)
        let a = try store.save(png: Data([1]), json: "{}", originalPng: Data([2]), at: t)
        let b = try store.save(png: Data([3]), at: t)
        XCTAssertNotEqual(a, b)
        XCTAssertTrue(b.lastPathComponent.hasSuffix("_1.png"))
        let list = store.list()
        XCTAssertEqual(list.count, 2)
        XCTAssertEqual(list.first?.url, b)
        XCTAssertNotNil(list.last?.jsonURL); XCTAssertNotNil(list.last?.originalURL)
        _ = try store.save(png: Data([4]), at: t.addingTimeInterval(1))
        XCTAssertEqual(store.list().count, 2) // limit
        XCTAssertFalse(FileManager.default.fileExists(atPath: HistoryStore.sidecars(a).json.path)) // sidecars of pruned entry gone
        XCTAssertNil(HistoryStore.parseName("20250230_120000_000.png")) // impossible date
        XCTAssertNotNil(HistoryStore.parseName("20240229_235959_999_12.png"))
    }

    func testSelectionModelDragAndClamp() {
        let m = SelectionModel(bounds: PixelRect(x: 0, y: 0, width: 100, height: 100))
        m.rect = SelectionModel.fromPoints(PixelPoint(x: 50, y: 50), PixelPoint(x: 10, y: 20))
        XCTAssertEqual(m.rect, PixelRect(x: 10, y: 20, width: 41, height: 31))
        m.nudge(-50, 0); XCTAssertEqual(m.rect.x, 0)
        XCTAssertEqual(m.hitTest(PixelPoint(x: 0, y: 20), tolerance: 2), .topLeft)
        m.beginDrag(.right, at: PixelPoint(x: 41, y: 30))
        m.drag(to: PixelPoint(x: 99, y: 30)); XCTAssertEqual(m.rect.right, 100) // cursor at the edge reaches the last column
        m.drag(to: PixelPoint(x: -20, y: 30)); XCTAssertEqual(m.rect.width, 1) // flipped past the left edge collapses, not negative
    }

    func testSnapEngineFindsNearestEdgeAndGuides() {
        let (off, guides) = SnapEngine.snap(CGRect(x: 103, y: 0, width: 10, height: 10), [CGRect(x: 0, y: 50, width: 100, height: 10)], threshold: 5)
        XCTAssertEqual(off.dx, -3)
        XCTAssertEqual(guides.first?.orientation, .vertical)
        XCTAssertEqual(SnapEngine.snap(CGRect(x: 200, y: 0, width: 1, height: 1), [CGRect(x: 0, y: 50, width: 10, height: 10)], threshold: 5).0, .zero)
    }

    func testHudPlacementPrefersBelowThenAbove() {
        let work = CGRect(x: 0, y: 0, width: 1000, height: 800)
        XCTAssertTrue(HudLayout.place(selection: CGRect(x: 10, y: 10, width: 100, height: 100), work: work, toolbar: CGSize(width: 500, height: 40)).below)
        let (p, below) = HudLayout.place(selection: CGRect(x: 900, y: 600, width: 100, height: 190), work: work, toolbar: CGSize(width: 500, height: 40))
        XCTAssertFalse(below)
        XCTAssertEqual(p.x, 1000 - 500 - 8) // kept inside the work area
    }

    func testOcrLayoutSelectionAndText() {
        let w = { (t: String, x: CGFloat, y: CGFloat) in OcrWord(text: t, box: CGRect(x: x, y: y, width: 20, height: 10)) }
        let l = OcrLayout([OcrLine(words: [w("Hello", 0, 0), w("world", 30, 0)], text: "Hello world"),
                           OcrLine(words: [w("Второй", 0, 20)], text: "Второй")])
        XCTAssertEqual(l.wordAt(CGPoint(x: 35, y: 5)), 1)
        XCTAssertEqual(l.lineRange(0), [0, 1])
        XCTAssertEqual(l.textOf([]), "Hello world\nВторой")
        XCTAssertEqual(l.textOf([1, 2]), "world\nВторой")
        XCTAssertEqual(l.selectionRuns([0, 1]).count, 1)
        XCTAssertEqual(l.inRect(CGRect(x: 0, y: 15, width: 5, height: 10)), [2])
    }

    func testEffectsChangePixelsOnlyInside() {
        var px = [UInt8](repeating: 0, count: 8 * 8 * 4)
        for i in stride(from: 0, to: px.count, by: 4) { let v = UInt8((i / 4) % 2 == 0 ? 255 : 0); px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255 }
        let img = PixelImage(pixels: px, width: 8, height: 8)
        let doc = AnnotationDocument()
        doc.add(PixelateAnnotation(CGRect(x: 0, y: 0, width: 4, height: 8), style: style()))
        let out = PixelImage(cgImage: ImageComposer.compose(img, region: img.bounds, doc: doc))
        XCTAssertEqual(out.color(at: PixelPoint(x: 0, y: 0)), out.color(at: PixelPoint(x: 1, y: 0))) // averaged
        XCTAssertNotEqual(out.color(at: PixelPoint(x: 6, y: 0)), out.color(at: PixelPoint(x: 7, y: 0))) // untouched
        XCTAssertEqual(out.color(at: PixelPoint(x: 7, y: 7)), img.color(at: PixelPoint(x: 7, y: 7))) // orientation kept
    }

    func testBeautifyFrameIsOpaqueAndPadded() {
        let img = PixelImage(pixels: [UInt8](repeating: 255, count: 10 * 6 * 4), width: 10, height: 6).cgImage
        let out = PixelImage(cgImage: BeautifyComposer.compose(img, BeautifyOptions(preset: .blue, padding: .s, radius: 0, shadow: true)))
        XCTAssertEqual(out.width, 10 + 48); XCTAssertEqual(out.height, 6 + 48)
        XCTAssertEqual(out.color(at: PixelPoint(x: 0, y: 0)).a, 255)
        let c = out.color(at: PixelPoint(x: 0, y: 0)) // gradient starts top-left
        XCTAssertLessThanOrEqual(abs(Int(c.r) - 0x30) + abs(Int(c.g) - 0x78) + abs(Int(c.b) - 0xF6), 4)
        XCTAssertEqual(out.color(at: PixelPoint(x: 30, y: 26)), .white) // content in place
    }

    func testSettingsDecodeKeepsDefaultsForMissingAndBadFields() throws {
        let s = try JSONDecoder().decode(AppSettings.self, from: Data(#"{"historyLimit": 9999, "lastColor": "nope", "lastArrowStyle": "Weird"}"#.utf8))
        XCTAssertEqual(s.historyLimit, 500)
        XCTAssertEqual(s.lastColor, "#FF3B30")
        XCTAssertEqual(s.lastArrowStyle, .normal)
        XCTAssertEqual(s.hotkeys, AppSettings.defaultHotkeys)
    }

    func testLocMacifiesKeys() {
        XCTAssertEqual(Loc.macify("Copy (Ctrl+C), Alt to skip, Shift+click"), "Copy (⌘C), ⌥ Option to skip, ⇧click")
        XCTAssertEqual(Loc.macify("Altura"), "Altura")
    }

    func testShortcutKeysFollowLayout() {
        func ev(_ code: UInt16, _ ch: String) -> NSEvent {
            NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: .command, timestamp: 0, windowNumber: 0, context: nil,
                             characters: ch, charactersIgnoringModifiers: ch, isARepeat: false, keyCode: code)!
        }
        XCTAssertEqual(Key.code(ev(6, "z")), Key.z)    // US
        XCTAssertEqual(Key.code(ev(16, "z")), Key.z)   // German QWERTZ: the key labelled Z
        XCTAssertEqual(Key.code(ev(6, "y")), Key.y)
        XCTAssertEqual(Key.code(ev(6, "я")), Key.z)    // Russian: physical position
        XCTAssertNotEqual(Key.code(ev(0, "q")), Key.a) // French AZERTY: Q is not ⌘A
        XCTAssertEqual(Key.code(ev(123, "\u{F702}")), Key.left)
    }
}

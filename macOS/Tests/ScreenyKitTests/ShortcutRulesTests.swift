import XCTest
@testable import ScreenyKit

final class ShortcutRulesTests: XCTestCase {
    private let cmdShift = UInt((1 << 20) | (1 << 17))
    private var cmdShift2: HotkeySpec { HotkeySpec(keyCode: 19, modifiers: cmdShift) }

    override func setUp() { Loc.setLanguage("en") }

    func testValidListKeepsEnabledFlagsAndSkipsEmptyRows() {
        let r = ShortcutRules.check([ShortcutRow(spec: cmdShift2), ShortcutRow(spec: nil),
                                     ShortcutRow(spec: HotkeySpec(keyCode: 0, modifiers: cmdShift), enabled: false)])
        XCTAssertTrue(r.isValid)
        XCTAssertEqual(r.hotkeys, [cmdShift2, HotkeySpec(keyCode: 0, modifiers: cmdShift, enabled: false)])
    }

    func testDuplicateIgnoresEnabledFlag() {
        let r = ShortcutRules.check([ShortcutRow(spec: cmdShift2), ShortcutRow(spec: cmdShift2, enabled: false)])
        XCTAssertEqual(r.rowErrors, [1: Loc.t("Settings.DuplicateShortcut")])
        XCTAssertNil(r.listError)
        XCTAssertFalse(r.isValid)
    }

    func testKeyWithoutModifierIsRejected() {
        let r = ShortcutRules.check([ShortcutRow(spec: cmdShift2), ShortcutRow(spec: HotkeySpec(keyCode: 0, modifiers: 0))])
        XCTAssertNotNil(r.rowErrors[1])
        XCTAssertEqual(r.hotkeys, [cmdShift2])
    }

    func testNeedsOneEnabled() {
        XCTAssertNotNil(ShortcutRules.check([]).listError)
        XCTAssertNotNil(ShortcutRules.check([ShortcutRow(spec: cmdShift2, enabled: false)]).listError)
        XCTAssertNotNil(ShortcutRules.check([ShortcutRow(spec: nil)]).listError)
    }

    func testMacStringsExistInEveryLanguage() {
        for lang in Loc.languages {
            Loc.setLanguage(lang)
            for key in ["Mac.Settings.NeedOne", "Mac.Wizard.PermissionNote", "Mac.Settings.LanguageSystem"] {
                XCTAssertNotEqual(Loc.t(key), key, "\(lang) \(key)")
            }
        }
        Loc.setLanguage("en")
    }
}

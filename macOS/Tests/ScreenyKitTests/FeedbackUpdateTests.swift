import XCTest
@testable import ScreenyKit

final class FeedbackTests: XCTestCase {
    let ctx = ScrubContext(home: "/Users/daniel", userName: "daniel")

    func testScrubReplacesHomeOtherUsersAndName() {
        XCTAssertEqual(LogScrubber.scrub("open /Users/daniel/Library/x.png failed", ctx), "open ~/Library/x.png failed")
        XCTAssertEqual(LogScrubber.scrub("\"/Users/Daniel\"", ctx), "\"~\"")
        XCTAssertEqual(LogScrubber.scrub("/Users/someone/Desktop/a.png", ctx), "/Users/<user>/Desktop/a.png")
        XCTAssertEqual(LogScrubber.scrub("/Volumes/Ext/daniel/History", ctx), "/Volumes/Ext/<user>/History")
        // A longer name that only starts with the user name is another user, not the home folder.
        XCTAssertEqual(LogScrubber.scrub("/Users/danielle/x", ctx), "/Users/<user>/x")
        XCTAssertEqual(LogScrubber.scrub("daniel wrote nothing", ctx), "daniel wrote nothing")
        XCTAssertEqual(LogScrubber.scrub("x Dev text mode: secret", ctx), LogScrubber.removedLine)
        let long = LogScrubber.scrub(String(repeating: "a", count: 600), ctx)
        XCTAssertEqual(long.count, LogScrubber.maxLineLength + 1)
    }

    func testTailKeepsLastLines() {
        XCTAssertEqual(LogScrubber.tail(["1", "2", "3"], ctx, count: 2), "2\n3")
    }

    func testValidation() {
        XCTAssertEqual(FeedbackRules.checkTitle("  ab "), .tooShort)
        XCTAssertNil(FeedbackRules.checkTitle("abc"))
        XCTAssertEqual(FeedbackRules.checkTitle(String(repeating: "x", count: 121)), .tooLong)
        XCTAssertEqual(FeedbackRules.checkDescription("short"), .tooShort)
        XCTAssertNil(FeedbackRules.checkContact(nil))
        XCTAssertNil(FeedbackRules.checkContact("   "))
        XCTAssertEqual(FeedbackRules.checkContact(String(repeating: "x", count: 201)), .tooLong)
        XCTAssertTrue(FeedbackRules.isValid(title: "Bug here", description: "It crashes every time", contact: ""))
        XCTAssertFalse(FeedbackRules.isValid(title: "Bug here", description: "", contact: ""))
    }

    func testRequestJsonShape() throws {
        let r = FeedbackRequest(kind: .bug, title: " T1t ", description: "Desc", contact: "  ", diagnostics: nil, installId: "id")
        let obj = try JSONSerialization.jsonObject(with: r.json()) as! [String: Any]
        XCTAssertEqual(obj["kind"] as? String, "bug")
        XCTAssertEqual(obj["title"] as? String, "T1t")
        XCTAssertTrue(obj["contact"] is NSNull)
        XCTAssertTrue(obj["diagnostics"] is NSNull)
        XCTAssertEqual(obj["installId"] as? String, "id")
    }

    func testResponseMapping() {
        XCTAssertEqual(FeedbackClient.map(status: 201, body: #"{"id":" K7Q2M "}"#, retryAfterHeader: nil, now: Date()), .sent(id: "K7Q2M"))
        XCTAssertEqual(FeedbackClient.map(status: 400, body: "nope", retryAfterHeader: nil, now: Date()), .invalid(message: nil))
        XCTAssertEqual(FeedbackClient.map(status: 429, body: nil, retryAfterHeader: "90", now: Date()), .rateLimited(retryAfter: 90))
        XCTAssertEqual(FeedbackClient.retryMinutes(90), 2)
        XCTAssertEqual(FeedbackClient.map(status: 503, body: nil, retryAfterHeader: nil, now: Date()), .serverError(503))
        XCTAssertNil(FeedbackEndpoint.choose(isDev: true, explicit: nil))
        XCTAssertEqual(FeedbackEndpoint.choose(isDev: false, explicit: "http://x"), FeedbackEndpoint.production)
        XCTAssertTrue(GitHubIssueLink.build(.idea, title: "a&b #1").absoluteString.hasSuffix("?template=feature_request.yml&title=a%26b%20%231"))
    }
}

final class UpdateTests: XCTestCase {
    func testVersionCompare() {
        let c = UpdateChecker.compareVersions
        XCTAssertEqual(c("v1.2.10", "1.2.9"), .orderedDescending)
        XCTAssertEqual(c("1.2", "1.2.0"), .orderedSame)
        XCTAssertEqual(c("1.0.0-beta.1", "1.0.0"), .orderedAscending)
        XCTAssertEqual(c("1.0.0-beta.2", "1.0.0-beta.11"), .orderedAscending)
        XCTAssertEqual(c("1.0.0-alpha", "1.0.0-alpha.1"), .orderedAscending)
        XCTAssertEqual(c("1.0.0-1", "1.0.0-alpha"), .orderedAscending)
        XCTAssertEqual(c("1.0.0+abc", "1.0.0"), .orderedSame)
        XCTAssertEqual(c("0.3.0", "0.2.9"), .orderedDescending)
    }

    func testReasonKeys() {
        XCTAssertEqual(UpdateChecker.reasonKey(error: URLError(.timedOut), status: nil), "About.ReasonTimeout")
        XCTAssertEqual(UpdateChecker.reasonKey(error: URLError(.notConnectedToInternet), status: nil), "About.ReasonNetwork")
        XCTAssertEqual(UpdateChecker.reasonKey(error: nil, status: 403), "About.ReasonLimit")
        XCTAssertEqual(UpdateChecker.reasonKey(error: nil, status: 502), "About.ReasonServer")
        XCTAssertNil(UpdateChecker.reasonKey(error: nil, status: 404))
    }

    func testParseRelease() {
        let json = #"{"tag_name":"v0.4.0","body":"- a","html_url":"https://github.com/DontIgnore/Screeny/releases/tag/v0.4.0"}"#
        let r = UpdateChecker.parseRelease(Data(json.utf8))
        XCTAssertEqual(r?.version, "0.4.0")
        XCTAssertEqual(r?.notes, "- a")
    }

    func testNotesParsing() {
        let md = """
        ## New <!-- feature: ocr -->
        - **Bold** and `code` and [link](http://x) _it_
          continued
        1. first
          - nested
        ### Fixes
        <!-- feature: pins -->
        - pin fix
        - general
        """
        let b = ReleaseNotes.parse(md)
        XCTAssertEqual(b.map(\.kind), [.heading2, .bullet, .numbered, .bullet, .heading3, .bullet, .bullet])
        XCTAssertEqual(b[1].inlines, [NoteInline("Bold", bold: true), NoteInline(" and "), NoteInline("code", code: true), NoteInline(" and link it continued")])
        XCTAssertEqual(b[0].features, ["ocr"])
        XCTAssertEqual(b[1].features, ["ocr"])
        XCTAssertEqual(b[3].level, 1)
        XCTAssertEqual(b[5].features, ["pins"])
        XCTAssertEqual(b[6].features, ["ocr"]) // the heading's section continues (### is inside ##)
        XCTAssertEqual(b[2].number, 1)

        let off = ReleaseNotes.filter(b) { $0 != "ocr" }
        // Headings stay when a block with its own enabled tag remains under them.
        XCTAssertEqual(off.map(\.plainText), ["New", "Fixes", "pin fix"])
        XCTAssertEqual(ReleaseNotes.filter(b) { $0 == "ocr" }.count, 6)
    }

    func testFeatureFilterUsesSettings() {
        let s = AppSettings()
        s.set(.ocr, on: false)
        let md = "- general\n- text <!-- feature: ocr -->\n- precise <!-- feature: ocr-precise -->"
        XCTAssertEqual(UpdateChecker.visibleNotes(md, settings: s, language: "en").map(\.plainText), ["general"])
        // Everything filtered out: the full list is shown (versions in between are unknown).
        XCTAssertEqual(UpdateChecker.visibleNotes("- text <!-- feature: ocr -->", settings: s, language: "en").map(\.plainText), ["text"])
    }

    func testLanguageSections() {
        let md = "Common\n<!-- lang: en -->\nHello\n<!-- lang: zh-CN -->\n你好\n<!-- lang: ru-RU -->\nПривет"
        XCTAssertEqual(ReleaseNotes.forLanguage(md, "zh-Hans"), "Common\n你好\n")
        XCTAssertEqual(ReleaseNotes.forLanguage(md, "ru"), "Common\nПривет\n")
        XCTAssertEqual(ReleaseNotes.forLanguage(md, "ja"), "Common\nHello\n")
        XCTAssertEqual(ReleaseNotes.languages(md), ["en", "zh-CN", "ru-RU"])
    }
}

final class MacStringsTests: XCTestCase {
    private func load(_ url: URL) throws -> [String: String] {
        try XCTUnwrap(try JSONSerialization.jsonObject(with: Data(contentsOf: url)) as? [String: String], url.lastPathComponent)
    }

    private func placeholders(_ s: String) -> Set<String> {
        let rx = try! NSRegularExpression(pattern: #"\{\d+\}"#)
        return Set(rx.matches(in: s, range: NSRange(s.startIndex..., in: s)).map { (s as NSString).substring(with: $0.range) })
    }

    func testMacOverridesParseAndMatchEnglish() throws {
        let strings = try XCTUnwrap(Loc.bundle.resourceURL?.appendingPathComponent("Strings"))
        let en = try load(strings.appendingPathComponent("en.json"))
        let macEn = try load(strings.appendingPathComponent("mac/en.json"))
        for lang in Loc.languages {
            let mac = try load(strings.appendingPathComponent("mac/\(lang).json"))
            for (key, value) in mac {
                let isMacKey = key.hasPrefix("Mac.")
                XCTAssertTrue(isMacKey || en[key] != nil, "\(lang): unknown key \(key)")
                let english = isMacKey ? macEn[key] : en[key]
                XCTAssertNotNil(english, "\(lang): \(key) has no English text")
                if let english { XCTAssertEqual(placeholders(value), placeholders(english), "\(lang): placeholders of \(key)") }
            }
        }
    }
}

private final class StubServices: AppServices {
    let settings = AppSettings()
    let history = HistoryStore(folder: FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString), limit: 10)
    let historyChanged = Notification.Name("test.feedback.historyChanged")
    func saveSettings() -> Bool { true }
    func startCapture() {}
    func showHistory() {}
    func showSettings() {}
    func showSetupWizard() {}
    func showFeedback(bug: Bool) {}
    func checkForUpdates(interactive: Bool) {}
    func openEditor(_ item: HistoryItem) {}
    func pin(_ image: CGImage, scale: CGFloat) {}
    func copyImage(_ image: CGImage, scale: CGFloat) -> Bool { true }
    func toast(_ text: String) {}
    var hotkeyText: String? { nil }
}

final class FeedbackUpdateWindowSmokeTests: XCTestCase {
    func testWindowsBuildAndFit() throws {
        for bug in [true, false] {
            let c = FeedbackWindowController(services: StubServices(), bug: bug)
            c.show()
            let h = try XCTUnwrap(c.window?.frame.height)
            XCTAssertGreaterThan(h, 300)
            XCTAssertLessThan(h, 1000)
            c.close()
        }
        let notes = ReleaseNotes.parse("# Title\n- **one** `x`\n  - two\n1. three\nText")
        let release = ReleaseInfo(version: "9.9.9", notes: nil, pageURL: URL(string: "https://example.com")!)
        let u = UpdateWindowController(release: release, notes: notes, current: "1.0.0") { true }
        u.show(activate: false)
        XCTAssertGreaterThan(try XCTUnwrap(u.window?.frame.height), 200)
        XCTAssertTrue(UpdateWindowController.attributed(notes).string.contains("•\tone x"))
        u.close()
        XCTAssertTrue(FeedbackWindowController.previewText(FeedbackDiagnosticsCollector.collect()).contains("macOS"))
    }
}

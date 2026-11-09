import AppKit

/// QA without Screen Recording permission (`Screeny --dev-render <scenario> <out.png> [fixture.png]`):
/// builds the real overlay / editor / windows over a fixture picture, drives them like a user would,
/// and saves what they draw. Mirrors OverlayWindow.Dev.cs.
enum DevScenarios {
    static func handle(_ args: [String], app: AppDelegate) -> Bool {
        guard let i = args.firstIndex(of: "--dev-render"), args.count > i + 2 else { return false }
        let scenario = args[i + 1], out = URL(fileURLWithPath: args[i + 2])
        let fixture = args.count > i + 3 ? PixelImage.load(url: URL(fileURLWithPath: args[i + 3])) : nil
        let env = ProcessInfo.processInfo.environment
        Loc.setLanguage(env["SCREENY_LANG"] ?? app.settings.language)
        if let a = env["SCREENY_APPEARANCE"] { NSApp.appearance = NSAppearance(named: a == "light" ? .aqua : .darkAqua) }
        // A timer, not the main queue: the scenario pumps the run loop and must let main-queue work run.
        Timer.scheduledTimer(withTimeInterval: 0.05, repeats: false) { _ in
            do { try run(scenario, out: out, fixture: fixture, app: app) } catch {
                FileHandle.standardError.write(Data("dev-render failed: \(error)\n".utf8))
                exit(1)
            }
        }
        return true
    }

    static func fixtureShot(_ fixture: PixelImage?) -> DisplayShot {
        let screen = NSScreen.main!
        let scale = screen.backingScaleFactor
        let w = Int(screen.frame.width * scale), h = Int(screen.frame.height * scale)
        let ctx = CGContext(data: nil, width: w, height: h, bitsPerComponent: 8, bytesPerRow: 0, space: PixelImage.colorSpace, bitmapInfo: PixelImage.bitmapInfo)!
        ctx.setFillColor(RGBA(hex: 0xEDEFF3).cgColor); ctx.fill(CGRect(x: 0, y: 0, width: w, height: h))
        if let f = fixture {
            let k = min(CGFloat(w) / CGFloat(f.width), CGFloat(h) / CGFloat(f.height))
            ctx.interpolationQuality = .high
            ctx.draw(f.cgImage, in: CGRect(x: 0, y: CGFloat(h) - CGFloat(f.height) * k, width: CGFloat(f.width) * k, height: CGFloat(f.height) * k))
        }
        let img = PixelImage(cgImage: ctx.makeImage()!, scale: scale)
        let windows = [WindowInfo(rect: PixelRect(x: Int(80 * scale), y: Int(120 * scale), width: Int(900 * scale), height: Int(560 * scale)))]
        return DisplayShot(screen: screen, displayID: CGMainDisplayID(), image: img, windows: windows)
    }

    static func pump(_ seconds: Double) { RunLoop.main.run(until: Date().addingTimeInterval(seconds)) }

    static func save(_ view: NSView, _ out: URL) throws {
        view.layoutSubtreeIfNeeded()
        view.displayIfNeeded()
        guard let rep = view.bitmapImageRepForCachingDisplay(in: view.bounds) else { throw CocoaError(.fileWriteUnknown) }
        view.cacheDisplay(in: view.bounds, to: rep)
        // The window background isn't part of the content view: put it under, resolved for the current appearance.
        let img = NSImage(size: view.bounds.size)
        img.lockFocus()
        view.effectiveAppearance.performAsCurrentDrawingAppearance {
            (view.window?.backgroundColor ?? .windowBackgroundColor).setFill()
            CGRect(origin: .zero, size: view.bounds.size).fill()
        }
        rep.draw(in: CGRect(origin: .zero, size: view.bounds.size))
        img.unlockFocus()
        let tiff = img.tiffRepresentation!
        try NSBitmapImageRep(data: tiff)!.representation(using: .png, properties: [:])!.write(to: out)
    }

    /// End-to-end: real window, real event routing (window.sendEvent → hitTest → view), like a user's mouse and keys.
    static func flow(app: AppDelegate, fixture: PixelImage?, out: URL) throws {
        let settings = app.settings
        settings.beautifyEnabled = false
        let shot = fixtureShot(fixture)
        let session = CaptureSession(shots: [shot], settings: settings, previousApp: nil)
        var result: CaptureResult?
        var finished = false
        session.finished = { r in result = r; finished = true }
        session.show()
        let w = session.windows[0], v = session.views[0]
        w.alphaValue = 0.01
        pump(0.3)
        let H = w.frame.height
        var failures: [String] = []
        func check(_ ok: Bool, _ what: String) { print((ok ? "PASS " : "FAIL ") + what); if !ok { failures.append(what) } }
        func mouse(_ type: NSEvent.EventType, _ x: CGFloat, _ y: CGFloat, clicks: Int = 1, mods: NSEvent.ModifierFlags = []) {
            let e = NSEvent.mouseEvent(with: type, location: CGPoint(x: x, y: H - y), modifierFlags: mods, timestamp: ProcessInfo.processInfo.systemUptime,
                                       windowNumber: w.windowNumber, context: nil, eventNumber: 0, clickCount: clicks, pressure: 1)!
            w.sendEvent(e)
        }
        func drag(_ a: CGPoint, _ b: CGPoint) {
            mouse(.leftMouseDown, a.x, a.y)
            for i in 1...8 { let t = CGFloat(i) / 8; mouse(.leftMouseDragged, a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t) }
            mouse(.leftMouseUp, b.x, b.y)
        }
        func key(_ code: UInt16, _ chars: String = "", mods: NSEvent.ModifierFlags = []) {
            for type in [NSEvent.EventType.keyDown, .keyUp] {
                let e = NSEvent.keyEvent(with: type, location: .zero, modifierFlags: mods, timestamp: ProcessInfo.processInfo.systemUptime,
                                         windowNumber: w.windowNumber, context: nil, characters: chars, charactersIgnoringModifiers: chars,
                                         isARepeat: false, keyCode: code)!
                if type == .keyDown && mods.contains(.command) { if !w.performKeyEquivalent(with: e) { w.sendEvent(e) } } else { w.sendEvent(e) }
            }
            pump(0.05)
        }
        check(w.isKeyWindow || w.firstResponder === v, "overlay window takes the keyboard")
        mouse(.mouseMoved, 300, 300)
        drag(CGPoint(x: 200, y: 200), CGPoint(x: 900, y: 650))
        check(!v.toolbar.isHidden, "drag selects an area and shows the toolbar")
        check(v.hole?.width == Int(700 * shot.scale) + 1 || v.hole?.width == Int(700 * shot.scale), "selection size follows the drag (\(v.hole?.description ?? "-"))")
        key(23, "5")
        check(v.toolbar.currentTool == .arrow, "key 5 picks the arrow")
        drag(CGPoint(x: 300, y: 300), CGPoint(x: 500, y: 400))
        check(v.document.items.count == 1 && v.document.items[0] is ArrowAnnotation, "dragging draws an arrow")
        key(19, "2")
        drag(CGPoint(x: 600, y: 300), CGPoint(x: 700, y: 380))
        check(v.document.items.count == 2, "rectangle tool draws")
        key(6, "z", mods: .command)
        check(v.document.items.count == 1, "⌘Z undoes")
        key(6, "z", mods: [.command, .shift])
        check(v.document.items.count == 2, "⇧⌘Z redoes")
        key(28, "8")
        mouse(.leftMouseDown, 320, 500); mouse(.leftMouseUp, 320, 500)
        pump(0.1)
        if let tv = w.firstResponder as? NSTextView { tv.insertText("Привет", replacementRange: NSRange(location: NSNotFound, length: 0)); key(36, "\r") }
        check(v.document.items.contains { ($0 as? TextAnnotation)?.text == "Привет" }, "text tool types a label and Return commits it")
        key(18, "1")
        mouse(.leftMouseDown, 650, 300); mouse(.leftMouseUp, 650, 300)
        check(v.document.selected is RectAnnotation, "select tool picks the rectangle")
        key(51, "\u{7f}")
        check(!v.document.items.contains { $0 is RectAnnotation }, "Delete removes the selected shape")
        key(19, "2")
        key(53, "\u{1b}")
        check(v.toolbar.currentTool == .select && v.hole != nil, "Esc with a drawing tool goes back to Select and keeps the selection")
        mouse(.rightMouseDown, 650, 300); mouse(.rightMouseUp, 650, 300)
        check(v.hole == nil && v.document.items.count == 2, "right click then drops the selection, markup stays")
        drag(CGPoint(x: 200, y: 200), CGPoint(x: 900, y: 650))
        // Click the toolbar's Copy button through real hit-testing.
        let copy = v.toolbar.subviews.compactMap { $0 as? HudButton }.first { $0.accent }!
        let c = copy.convert(CGPoint(x: copy.bounds.midX, y: copy.bounds.midY), to: v)
        mouse(.leftMouseDown, c.x, c.y); mouse(.leftMouseUp, c.x, c.y)
        pump(0.3)
        check(finished && result?.action == .copyExplicit, "Copy button finishes with a copy")
        if let r = result {
            check(r.image.width == v.hole?.width ?? r.region.width, "result is the selected region")
            check(r.annotationsJSON?.contains("\"arrow\"") == true, "markup document goes with the result")
            check(Clipboard.copy(r.image, scale: r.scale) && NSPasteboard.general.data(forType: .png) != nil, "picture lands on the clipboard as PNG")
            try PixelImage.encode(r.image, type: .png)?.write(to: out)
        }
        print(failures.isEmpty ? "ALL PASSED" : "FAILED: \(failures.count)")
        exit(failures.isEmpty ? 0 : 1)
    }

    static func run(_ scenario: String, out: URL, fixture: PixelImage?, app: AppDelegate) throws {
        let settings = app.settings
        settings.beautifyEnabled = scenario.hasPrefix("beautify")
        switch scenario {
        case "select", "draw", "beautify", "text", "pipette", "tools":
            let shot = fixtureShot(fixture)
            let w = OverlayWindow(screen: shot.screen)
            let v = OverlayView(shot: shot, settings: settings)
            w.contentView = v
            w.orderFrontRegardless()
            w.alphaValue = 0.01
            let s = shot.scale
            func px(_ x: CGFloat, _ y: CGFloat) -> CGPoint { CGPoint(x: x * s, y: y * s) }
            if scenario == "select" {
                v.devSetHint(shot.windows.first?.rect)
                v.devMagnifier(at: PixelPoint(x: Int(400 * s), y: Int(300 * s)))
            } else {
                v.devSelect(PixelRect(x: Int(120 * s), y: Int(140 * s), width: Int(820 * s), height: Int(500 * s)))
                let ed = v.editor!
                func style(_ t: ToolKind) -> AnnotationStyle { ed.style(for: t) }
                if scenario == "draw" || scenario == "beautify" {
                    ed.document.add(RectAnnotation(CGRect(origin: px(160, 190), size: CGSize(width: 220 * s, height: 120 * s)), style: style(.rect)))
                    ed.document.add(ArrowAnnotation(px(420, 260), px(700, 360), style: style(.arrow)))
                    var ts = style(.text); ts.textBackground = Palette.colors[0]; ts.color = .white
                    ed.document.add(TextAnnotation(px(450, 200), text: "Best week so far", style: ts))
                    ed.document.add(NumberAnnotation(px(200, 420), number: 1, style: style(.number)))
                    ed.document.add(NumberAnnotation(px(260, 420), number: 2, style: style(.number)))
                    ed.document.add(HighlighterAnnotation(px(560, 480), style: style(.highlighter)))
                    (ed.document.items.last as? PenAnnotation)?.addPoint(px(860, 480))
                    ed.document.add(BlurAnnotation(CGRect(origin: px(600, 540), size: CGSize(width: 240 * s, height: 70 * s)), style: style(.blur)))
                    ed.document.add(PixelateAnnotation(CGRect(origin: px(160, 540), size: CGSize(width: 200 * s, height: 70 * s)), style: style(.pixelate)))
                    ed.document.add(EllipseAnnotation(CGRect(origin: px(400, 520), size: CGSize(width: 150 * s, height: 90 * s)), style: style(.ellipse)))
                    if scenario == "draw" { ed.setTool(.rect); ed.flyouts.openOptionsAtTool() }
                    else { ed.setBeautify(true); ed.flyouts.openBeautify() }
                }
                if scenario == "pipette" { ed.setTool(.arrow); ed.setPipette(true); v.devMagnifier(at: PixelPoint(x: Int(500 * s), y: Int(400 * s))) }
                if scenario == "tools" { ed.setTool(.arrow); ed.flyouts.openOptionsAtTool() }
                if scenario == "text" {
                    v.devEnterTextMode()
                    let deadline = Date().addingTimeInterval(15)
                    while !v.devTextReady && Date() < deadline { pump(0.1) }
                    v.devSelectAllText()
                }
            }
            pump(0.5)
            try save(v, out)
            if scenario == "draw" {
                // The exported picture, as it would be copied.
                let img = ImageComposer.compose(shot.image, region: PixelRect(x: Int(120 * s), y: Int(140 * s), width: Int(820 * s), height: Int(500 * s)),
                                                doc: v.document)
                try PixelImage.encode(BeautifyComposer.compose(img, .default), type: .png)?.write(to: out.deletingPathExtension().appendingPathExtension("export.png"))
            }
        case "flow":
            try flow(app: app, fixture: fixture, out: out)
        case "editor":
            let shot = fixtureShot(fixture)
            let folder = FileManager.default.temporaryDirectory.appendingPathComponent("screeny-dev-history")
            try? FileManager.default.removeItem(at: folder)
            let store = HistoryStore(folder: folder, limit: 10)
            let crop = shot.image.cropped(PixelRect(x: 200, y: 200, width: 1600, height: 1000))
            let a = ArrowAnnotation(CGPoint(x: 200, y: 200), CGPoint(x: 700, y: 500), style: AnnotationStyle(color: Palette.colors[0], thickness: 4))
            let json = AnnotationSerializer.serialize([a], regionOrigin: .zero, width: crop.width, height: crop.height, beautify: nil)
            let doc = AnnotationDocument(); doc.load([a])
            let png = PixelImage.encode(ImageComposer.compose(crop, region: crop.bounds, doc: doc), type: .png)!
            let url = try store.save(png: png, json: json, originalPng: crop.pngData())
            let item = store.list().first { $0.url == url }!
            guard let c = EditorWindowController(item: item, services: app) else { throw CocoaError(.fileReadCorruptFile) }
            c.window?.alphaValue = 0.01
            c.showWindow(nil)
            pump(0.6)
            try save(c.window!.contentView!, out)
        case "history", "settings", "wizard":
            let wc: NSWindowController
            switch scenario {
            case "history": let h = HistoryWindowController(services: app); h.show(); wc = h
            case "settings": let s = SettingsWindowController(services: app); s.show(); wc = s
            default: let s = SetupWizardWindowController(services: app, onFinish: {}); s.show(); wc = s
            }
            wc.window?.alphaValue = 0.01
            pump(0.8)
            try save(wc.window!.contentView!, out)
        default:
            throw CocoaError(.featureUnsupported)
        }
        exit(0)
    }
}

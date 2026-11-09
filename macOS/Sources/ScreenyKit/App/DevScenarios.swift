import AppKit

/// QA without Screen Recording permission (`Screeny --dev-render <scenario> <out.png> [fixture.png]`):
/// builds the real overlay / editor / windows over a fixture picture, drives them like a user would,
/// and saves what they draw. Mirrors OverlayWindow.Dev.cs.
enum DevScenarios {
    static func handle(_ args: [String], app: AppDelegate) -> Bool {
        if args.contains("--dev-capture") {
            // The normal app, plus one capture through the shortcut's handler two seconds after launch.
            Timer.scheduledTimer(withTimeInterval: 2, repeats: false) { _ in app.hotkeys.pressed?() }
            return false
        }
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
        return DisplayShot(frame: screen.frame, displays: [DisplayArea(frame: screen.frame, visibleFrame: screen.visibleFrame)], image: img, windows: windows)
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
    /// `live`: the real thing. Presses the capture shortcut's handler, checks the overlay opens over a real screenshot,
    /// closes it with Esc, then runs `flow` over that real screenshot.
    static func live(app: AppDelegate, out: URL) {
        app.hotkeys.pressed?()
        let deadline = Date().addingTimeInterval(10)
        while app.session == nil && Date() < deadline { pump(0.1) }
        guard let s = app.session, let w = s.windows.first else { print("FAIL hotkey opens the overlay"); exit(1) }
        // Focus can't be judged here: pumping the run loop holds back AppKit's activation events (see `--dev-capture`).
        pump(1.0)
        let shot = s.master.shotInfo
        print("PASS hotkey opens the overlay (\(s.windows.count) window(s), visible: \(w.isVisible), \(shot.image.width)×\(shot.image.height))")
        let real = DisplayShot(frame: shot.frame, displays: shot.displays, image: shot.image, windows: shot.windows)
        print((real.windows.isEmpty ? "FAIL" : "PASS") + " window outlines found for hover snapping: \(real.windows.count)")
        let e = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0, windowNumber: w.windowNumber, context: nil,
                                 characters: "\u{1b}", charactersIgnoringModifiers: "\u{1b}", isARepeat: false, keyCode: 53)!
        w.sendEvent(e)
        pump(0.3)
        print((app.session == nil && !w.isVisible ? "PASS" : "FAIL") + " Esc closes the overlay")
        try? flow(app: app, shot: real, out: out, ocrNeedle: nil)
    }

    static func flow(app: AppDelegate, fixture: PixelImage?, out: URL) throws {
        try flow(app: app, shot: fixtureShot(fixture), out: out, ocrNeedle: "Q3 report")
    }

    static func flow(app: AppDelegate, shot: DisplayShot, out: URL, ocrNeedle: String?) throws {
        let settings = app.settings
        settings.beautifyEnabled = false
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
        // Second capture: text mode, then the frame and a pin.
        let s2 = CaptureSession(shots: [shot], settings: settings, previousApp: nil)
        var r2: CaptureResult?
        s2.finished = { r2 = $0 }
        s2.show()
        let w2 = s2.windows[0], v2 = s2.views[0]
        w2.alphaValue = 0.01
        pump(0.2)
        func key2(_ code: UInt16, _ chars: String, mods: NSEvent.ModifierFlags = []) {
            let e = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: mods, timestamp: 0, windowNumber: w2.windowNumber, context: nil,
                                     characters: chars, charactersIgnoringModifiers: chars, isARepeat: false, keyCode: code)!
            if mods.contains(.command) { if !w2.performKeyEquivalent(with: e) { w2.sendEvent(e) } } else { w2.sendEvent(e) }
            pump(0.05)
        }
        v2.devSelect(PixelRect(x: Int(100 * shot.scale), y: Int(150 * shot.scale), width: Int(800 * shot.scale), height: Int(300 * shot.scale)))
        key2(17, "t", mods: .command)
        let deadline = Date().addingTimeInterval(15)
        while !v2.devTextReady && Date() < deadline { pump(0.1) }
        check(v2.devTextReady, "⌘T recognizes text in the selection")
        key2(0, "a", mods: .command)
        key2(36, "\r")
        pump(0.2)
        check(r2?.action == .copyText && (ocrNeedle.map { (r2?.text ?? "").contains($0) } ?? !(r2?.text ?? "").isEmpty), "Enter copies the recognized text (\((r2?.text ?? "").prefix(40)))")
        let s3 = CaptureSession(shots: [shot], settings: settings, previousApp: nil)
        var r3: CaptureResult?
        s3.finished = { r3 = $0 }
        s3.show()
        let w3 = s3.windows[0], v3 = s3.views[0]
        w3.alphaValue = 0.01
        pump(0.2)
        v3.devSelect(PixelRect(x: 200, y: 300, width: 600, height: 400))
        for (code, ch) in [(UInt16(11), "b"), (UInt16(99), "")] {
            let e = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0, windowNumber: w3.windowNumber, context: nil,
                                     characters: ch, charactersIgnoringModifiers: ch, isARepeat: false, keyCode: code)!
            w3.sendEvent(e); pump(0.05)
        }
        pump(0.2)
        check(r3?.action == .pin, "B then F3 pins")
        check(r3.map { $0.image.width == 600 + 2 * 48 } == true, "the pin gets the background frame (\(r3?.image.width ?? 0) px)")
        if let r = r3 { app.pin(r.image, scale: r.scale, near: CGPoint(x: r.screenRect.minX, y: r.screenRect.maxY)) }
        pump(0.3)
        check(PinWindow.all.count == 1, "pin window opens")
        settings.beautifyEnabled = false
        print(failures.isEmpty ? "ALL PASSED" : "FAILED: \(failures.count)")
        exit(failures.isEmpty ? 0 : 1)
    }

    /// Two simulated displays (left and right halves of the screen, each with its own overlay window).
    static func twoDisplays(app: AppDelegate, fixture: PixelImage?, out: URL) throws {
        let settings = app.settings
        settings.beautifyEnabled = false
        let full = fixtureShot(fixture)
        let f = full.frame, half = f.width / 2, s = full.scale
        func part(_ x: CGFloat) -> DisplayShot {
            let fr = CGRect(x: f.minX + x, y: f.minY, width: half, height: f.height)
            let img = full.image.cropped(PixelRect(x: Int(x * s), y: 0, width: Int(half * s), height: full.image.height))
            return DisplayShot(frame: fr, displays: [DisplayArea(frame: fr, visibleFrame: fr.insetBy(dx: 0, dy: 30))], image: img, windows: [])
        }
        let session = CaptureSession(shots: [part(0), part(half)], settings: settings, previousApp: nil)
        var result: CaptureResult?
        session.finished = { result = $0 }
        session.show()
        session.windows.forEach { $0.alphaValue = 0.01 }
        pump(0.3)
        var failures: [String] = []
        func check(_ ok: Bool, _ what: String) { print((ok ? "PASS " : "FAIL ") + what); if !ok { failures.append(what) } }
        let v = session.master
        check(session.windows.count == 2 && session.windows.allSatisfy { $0.frame.width == half }, "one overlay window per display")
        check(v.shot.width == full.image.width, "virtual screen stitches both displays (\(v.shot.width) px)")
        // Drag from the left display into the right one: events go to the window where the drag started.
        let w = session.windows[0], H = w.frame.height
        func mouse(_ type: NSEvent.EventType, _ x: CGFloat, _ y: CGFloat, in win: NSWindow) {
            let loc = CGPoint(x: x - (win.frame.minX - f.minX), y: H - y)
            w.sendEvent(NSEvent.mouseEvent(with: type, location: loc, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                           windowNumber: win.windowNumber, context: nil, eventNumber: 0, clickCount: 1, pressure: 1)!)
        }
        func send(_ type: NSEvent.EventType, _ x: CGFloat, _ y: CGFloat, to win: NSWindow) {
            let loc = CGPoint(x: x - (win.frame.minX - f.minX), y: H - y)
            win.sendEvent(NSEvent.mouseEvent(with: type, location: loc, modifierFlags: [], timestamp: ProcessInfo.processInfo.systemUptime,
                                             windowNumber: win.windowNumber, context: nil, eventNumber: 0, clickCount: 1, pressure: 1)!)
        }
        _ = mouse
        let a = CGPoint(x: half - 300, y: 200), b = CGPoint(x: half + 300, y: 600)
        send(.leftMouseDown, a.x, a.y, to: w)
        for i in 1...10 { let t = CGFloat(i) / 10; send(.leftMouseDragged, a.x + (b.x - a.x) * t, a.y + (b.y - a.y) * t, to: w) }
        send(.leftMouseUp, b.x, b.y, to: w)
        pump(0.2)
        check(v.hole.map { abs($0.width - Int(600 * s)) <= 2 } == true, "selection spans the seam (\(v.hole?.description ?? "-"))")
        check(!v.toolbar.isHidden && v.window != nil, "toolbar is shown on the display holding the selection")
        // Draw an arrow on the other display's pane: its window forwards to the overlay view.
        let other = session.windows.first { $0 !== v.window }!
        let k = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0, windowNumber: v.window!.windowNumber, context: nil,
                                 characters: "5", charactersIgnoringModifiers: "5", isARepeat: false, keyCode: 23)!
        v.window!.sendEvent(k)
        // Inside the selection, on the display that only shows a pane.
        let ox = other.frame.minX - f.minX
        let inOther = ox < half ? half - 250 : half + 100
        let p1 = CGPoint(x: inOther, y: 300), p2 = CGPoint(x: inOther + 150, y: 450)
        send(.leftMouseDown, p1.x, p1.y, to: other)
        for i in 1...6 { let t = CGFloat(i) / 6; send(.leftMouseDragged, p1.x + (p2.x - p1.x) * t, p1.y + (p2.y - p1.y) * t, to: other) }
        send(.leftMouseUp, p2.x, p2.y, to: other)
        check(v.document.items.count == 1, "drawing on the other display's pane adds markup")
        for (i, win) in session.windows.enumerated() {
            try save(win.contentView!, out.deletingPathExtension().appendingPathExtension("display\(i).png"))
        }
        let ret = NSEvent.keyEvent(with: .keyDown, location: .zero, modifierFlags: [], timestamp: 0, windowNumber: v.window!.windowNumber, context: nil,
                                   characters: "\r", charactersIgnoringModifiers: "\r", isARepeat: false, keyCode: 36)!
        v.window!.sendEvent(ret)
        pump(0.3)
        if let r = result {
            check(r.image.width == v.hole?.width ?? -1 || abs(r.image.width - Int(600 * s)) <= 2, "result is the spanning region (\(r.image.width)×\(r.image.height))")
            // Pixels on both sides of the seam match the original picture.
            let img = PixelImage(cgImage: r.image)
            let left = full.image.color(at: PixelPoint(x: r.region.x + 5, y: r.region.y + 5)), right = full.image.color(at: PixelPoint(x: r.region.right - 5, y: r.region.y + 5))
            check(img.color(at: PixelPoint(x: 5, y: 5)) == left && img.color(at: PixelPoint(x: img.width - 5, y: 5)) == right, "both displays' pixels are in the result")
            try PixelImage.encode(r.image, type: .png)?.write(to: out)
        } else { check(false, "Enter finishes") }
        print(failures.isEmpty ? "ALL PASSED" : "FAILED: \(failures.count)")
        exit(failures.isEmpty ? 0 : 1)
    }

    static func run(_ scenario: String, out: URL, fixture: PixelImage?, app: AppDelegate) throws {
        let settings = app.settings
        settings.beautifyEnabled = scenario.hasPrefix("beautify")
        switch scenario {
        case "select", "draw", "beautify", "text", "pipette", "tools":
            let shot = fixtureShot(fixture)
            let w = OverlayWindow(frame: shot.frame)
            let v = OverlayView(shot: shot, settings: settings)
            w.contentView?.addSubview(v)
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
        case "selfupdate":
            // out = the release zip from pack.sh
            let app = try SelfUpdater.unpack(out)
            try SelfUpdater.verify(app)
            print("PASS release zip unpacks and is signed by the same developer")
            let p = Process(); p.executableURL = URL(fileURLWithPath: "/usr/bin/codesign")
            p.arguments = ["--force", "--deep", "--sign", "-", app.path]; try p.run(); p.waitUntilExit()
            do { try SelfUpdater.verify(app); print("FAIL ad-hoc build accepted"); exit(1) } catch { print("PASS foreign signature rejected") }
            exit(0)
        case "swap":
            // out = a copy of the app to replace; the zip path comes as the fixture argument
            let zip = URL(fileURLWithPath: CommandLine.arguments.last!)
            try SelfUpdater.scheduleSwap(new: try SelfUpdater.unpack(zip), replacing: out)
            exit(0)
        case "twodisplays":
            try twoDisplays(app: app, fixture: fixture, out: out)
        case "live": live(app: app, out: out)
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

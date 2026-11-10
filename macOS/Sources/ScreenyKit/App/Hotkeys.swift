import AppKit
import Carbon.HIToolbox

extension HotkeySpec {
    var flags: NSEvent.ModifierFlags { NSEvent.ModifierFlags(rawValue: modifiers) }

    /// "⌃⌥⇧⌘2" in macOS order.
    public var displayText: String {
        var s = ""
        if flags.contains(.control) { s += "⌃" }
        if flags.contains(.option) { s += "⌥" }
        if flags.contains(.shift) { s += "⇧" }
        if flags.contains(.command) { s += "⌘" }
        return s + HotkeySpec.keyName(keyCode)
    }

    static func keyName(_ code: UInt16) -> String {
        let special: [UInt16: String] = [
            36: "↩", 48: "⇥", 49: "Space", 51: "⌫", 53: "⎋", 117: "⌦", 123: "←", 124: "→", 125: "↓", 126: "↑",
            122: "F1", 120: "F2", 99: "F3", 118: "F4", 96: "F5", 97: "F6", 98: "F7", 100: "F8", 101: "F9", 109: "F10",
            103: "F11", 111: "F12", 105: "F13", 107: "F14", 113: "F15", 106: "F16", 64: "F17", 79: "F18", 80: "F19",
            115: "↖", 119: "↘", 116: "⇞", 121: "⇟",
        ]
        if let s = special[code] { return s }
        // The character on the US layout: shortcuts are physical keys, shown the same way as macOS shows them.
        let us: [UInt16: String] = [
            0: "A", 11: "B", 8: "C", 2: "D", 14: "E", 3: "F", 5: "G", 4: "H", 34: "I", 38: "J", 40: "K", 37: "L", 46: "M",
            45: "N", 31: "O", 35: "P", 12: "Q", 15: "R", 1: "S", 17: "T", 32: "U", 9: "V", 13: "W", 7: "X", 16: "Y", 6: "Z",
            18: "1", 19: "2", 20: "3", 21: "4", 23: "5", 22: "6", 26: "7", 28: "8", 25: "9", 29: "0", 27: "-", 24: "=",
            33: "[", 30: "]", 41: ";", 39: "'", 43: ",", 47: ".", 44: "/", 42: "\\", 50: "`",
        ]
        return us[code] ?? "#\(code)"
    }

    var carbonModifiers: UInt32 {
        var m: UInt32 = 0
        if flags.contains(.command) { m |= UInt32(cmdKey) }
        if flags.contains(.shift) { m |= UInt32(shiftKey) }
        if flags.contains(.option) { m |= UInt32(optionKey) }
        if flags.contains(.control) { m |= UInt32(controlKey) }
        return m
    }

    static let functionKeys: Set<UInt16> = [122, 120, 99, 118, 96, 97, 98, 100, 101, 109, 103, 111, 105, 107, 113, 106, 64, 79, 80]

    /// nil if fine, otherwise a localized reason (Hotkeys/HotkeyRules.cs).
    public var validationError: String? {
        let mods = flags.intersection([.command, .option, .control, .shift])
        if HotkeySpec.functionKeys.contains(keyCode) { return nil }
        if mods.isEmpty || mods == .shift { return Loc.t("Hotkeys.NeedModifier").replacingOccurrences(of: "Win", with: "⌘") }
        return nil
    }

    /// macOS screenshot shortcuts (⌘⇧3/4/5/6) — Screeny can take them only if they're turned off in System Settings.
    public var isSystemScreenshotShortcut: Bool {
        flags.intersection([.command, .shift, .option, .control]) == [.command, .shift] && [20, 21, 23, 22].contains(keyCode)
            || flags.intersection([.command, .shift, .option, .control]) == [.command, .shift, .control] && [20, 21].contains(keyCode)
    }
}

/// Global shortcuts through Carbon RegisterEventHotKey: needs no Accessibility permission.
final class HotkeyManager {
    private var refs: [EventHotKeyRef] = []
    private var handler: EventHandlerRef?
    var pressed: (() -> Void)?
    static var shared: HotkeyManager?

    init() {
        HotkeyManager.shared = self
        var spec = EventTypeSpec(eventClass: OSType(kEventClassKeyboard), eventKind: UInt32(kEventHotKeyPressed))
        InstallEventHandler(GetApplicationEventTarget(), { _, _, _ in
            DispatchQueue.main.async { HotkeyManager.shared?.pressed?() }
            return noErr
        }, 1, &spec, nil, &handler)
    }

    /// Registers enabled shortcuts; returns the ones that failed (taken by another app or the system).
    @discardableResult
    func register(_ specs: [HotkeySpec]) -> [HotkeySpec] {
        unregisterAll()
        var failed: [HotkeySpec] = []
        for (i, s) in specs.enumerated() where s.enabled {
            var ref: EventHotKeyRef?
            let id = EventHotKeyID(signature: OSType(0x5343_5259), id: UInt32(i + 1)) // 'SCRY'
            // Exclusive: fails instead of silently sharing a shortcut another app already holds.
            let status = RegisterEventHotKey(UInt32(s.keyCode), s.carbonModifiers, id, GetApplicationEventTarget(),
                                             OptionBits(kEventHotKeyExclusive), &ref)
            if status == noErr, let ref { refs.append(ref) } else { failed.append(s) }
        }
        return failed
    }

    func unregisterAll() {
        for r in refs { UnregisterEventHotKey(r) }
        refs.removeAll()
    }
}

/// Click-and-press shortcut field (Settings/HotkeyBox.cs). Backspace/Delete clears it.
final class HotkeyRecorder: NSView {
    var spec: HotkeySpec? { didSet { needsDisplay = true } }
    var changed: ((HotkeySpec?) -> Void)?
    private var recording = false { didSet { needsDisplay = true } }

    override var acceptsFirstResponder: Bool { true }
    override var intrinsicContentSize: NSSize { CGSize(width: 150, height: 28) }

    override func mouseDown(with event: NSEvent) {
        recording = true
        window?.makeFirstResponder(self)
        HotkeyManager.shared?.unregisterAll() // so the current shortcut can be pressed again
    }

    override func resignFirstResponder() -> Bool {
        if recording { recording = false; NotificationCenter.default.post(name: HotkeyRecorder.endedRecording, object: nil) }
        return true
    }

    static let endedRecording = Notification.Name("ScreenyHotkeyRecordingEnded")

    override func performKeyEquivalent(with event: NSEvent) -> Bool {
        guard recording else { return false }
        keyDown(with: event)
        return true
    }

    override func keyDown(with event: NSEvent) {
        guard recording else { super.keyDown(with: event); return }
        let mods = event.modifierFlags.intersection([.command, .option, .control, .shift])
        if mods.isEmpty && (event.keyCode == 51 || event.keyCode == 117) {
            spec = nil; changed?(nil); window?.makeFirstResponder(nil); return
        }
        if mods.isEmpty && event.keyCode == 53 { window?.makeFirstResponder(nil); return }
        let s = HotkeySpec(keyCode: event.keyCode, modifiers: mods.rawValue)
        spec = s
        changed?(s)
        window?.makeFirstResponder(nil)
    }

    override func draw(_ dirtyRect: NSRect) {
        let p = NSBezierPath(roundedRect: bounds.insetBy(dx: 1, dy: 1), xRadius: 7, yRadius: 7)
        Theme.card.setFill(); p.fill()
        (recording ? Theme.accent : Theme.strokeStrong).setStroke()
        p.lineWidth = recording ? 2 : 1
        p.stroke()
        let text = recording ? "…" : (spec?.displayText ?? "—")
        let s = NSAttributedString(string: text, attributes: [.font: NSFont.systemFont(ofSize: 13, weight: .medium), .foregroundColor: Theme.ink])
        let sz = s.size()
        s.draw(at: CGPoint(x: bounds.midX - sz.width / 2, y: bounds.midY - sz.height / 2))
    }
}

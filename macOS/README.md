# Screeny for macOS

A native AppKit port of Screeny with the same overlay, toolbar, markup tools, text mode, history, pins, background frame,
settings and five interface languages as the Windows app. It lives in the menu bar.

## Build and install

You need macOS 14 or later on Apple silicon and Xcode (Swift 6).

```bash
cd macOS
./build.sh            # builds and signs macOS/build/Screeny.app
./build.sh --install  # also copies it to ~/Applications/Screeny.app (quits a running copy first)
./build.sh --run      # install and launch
swift test            # unit tests
```

### Screen Recording permission

macOS asks for Screen Recording permission once. Screeny shows a setup window with a "Grant access" button that
opens System Settings › Privacy & Security › Screen & System Audio Recording. Turn Screeny on there; macOS may offer
to quit and reopen it.

macOS remembers the permission for the app's **code signature**. `build.sh` therefore signs with the same certificate
every time (the first "Apple Development" or "Developer ID Application" identity in your keychain, or
`SCREENY_SIGN_IDENTITY`). With that, rebuilding and reinstalling keeps the permission. Without a certificate the build
falls back to ad-hoc signing, and macOS forgets the permission after every rebuild — that was the main problem of the
first port attempt.

## Differences from Windows

| Windows | macOS |
|---|---|
| PrintScreen | ⌘⇧2 (change or add shortcuts in Settings; ⌘⇧3/4/5 belong to macOS unless you turn them off in System Settings › Keyboard › Keyboard Shortcuts › Screenshots) |
| Ctrl+C / Ctrl+S / Ctrl+T / Ctrl+Z / Ctrl+Y | ⌘C / ⌘S / ⌘T / ⌘Z / ⇧⌘Z (⌘Y works too) |
| Alt (no snapping while dragging) | ⌥ Option |
| Tray icon: click captures, right click opens the menu | Menu bar icon: click captures, right click (or ⌃/⌥ click) opens the menu |
| Windows OCR + optional PaddleOCR module | Apple Vision text recognition, built in (no module to download) |
| Conflicts wizard (Lightshot, ShareX…) | Setup window: Screen Recording permission, shortcuts, features, updates, open at login |
| Velopack self-update | Update check against GitHub Releases; the download page opens in the browser |

Annotation thickness and text size are chosen in screen points, so markup looks the same size as on a 100 % Windows
display; the files store pixels like on Windows.

## Where data lives

| What | Where |
|---|---|
| Settings | `~/Library/Application Support/Screeny/settings.json` |
| History (PNG + `.screeny.json` + optional `.orig.png`, same format as Windows) | `~/Library/Application Support/Screeny/History/` |
| Error log | `~/Library/Logs/Screeny/screeny.log` |

## QA without clicking

`Screeny.app/Contents/MacOS/Screeny --dev-render <scenario> <out.png> [fixture.png]` builds the real views over a
fixture picture and saves what they draw. Scenarios: `select`, `draw`, `tools`, `beautify`, `pipette`, `text`
(real OCR), `editor`, `history`, `settings`, `wizard`, and `flow` — an end-to-end run that sends real mouse and key
events through the overlay window (select, draw, undo/redo, type text, delete, Esc, copy) and prints PASS/FAIL.
`SCREENY_LANG=en` and `SCREENY_APPEARANCE=light|dark` override language and appearance. `Tests/Fixtures/desktop.png`
is a ready fixture.

## Known limitations

- A selection stays on one display; a shot across two monitors isn't supported yet (Windows allows it).
- Not tested on Intel Macs or macOS 14/15 hardware, nor with several monitors of different scale.
- macOS 15+ may periodically ask whether Screeny should keep recording the screen; that prompt comes from the system.

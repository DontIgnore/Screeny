# Notes for maintainers

This page covers what you need to work on Screeny itself: the dev instance, releases, release notes and a few design
decisions that are easy to undo by accident. The user-facing docs are in the [README](../README.md).

## How capture works

Screeny grabs the whole virtual screen with GDI (`BitBlt`) **before** it shows the overlay, so the overlay never ends
up in the picture. The overlay is a single window the size of the virtual screen, placed with `SetWindowPos` in device
pixels. The selection and all shapes are stored in screenshot pixels and drawn through `ScaleTransform(1/dpi)`. Export
renders the same vector shapes with the same code at 96 DPI, so the clipboard image matches what was on screen.

## Dev instance

`--dev` (or the environment variable `SCREENY_DEV=1`) starts a test instance that can run next to your normal copy.
It has its own mutex, settings (`settings.dev.json`), history (`%LocalAppData%\Screeny\Dev\History\`), update state
(`update-state.dev.json`) and module folder (`%LocalAppData%\Screeny\Dev\Modules\`). It registers no regular capture hotkeys
(only the Win+Shift+S hook, with `--enable-ll-hook` and if it's on in `settings.dev.json`) and never touches the real
autostart entry. Without `--update-source` it doesn't look for updates, without
`--module-source` it doesn't download the OCR module, and without `--feedback-endpoint` it doesn't send bug reports or
ideas ("Send" says the endpoint isn't set), so it makes no network requests.

```powershell
dotnet run --project src/Screeny -- --settings          # open settings right away (or --history)
dotnet run --project src/Screeny -- --dev --overlay-test shot.png [--select x,y,w,h] [--scenario <name>]
dotnet run --project src/Screeny -- --dev --edit <history shot.png> [--scenario <name>]
dotnet run --project src/Screeny -- --dev --history [--scenario hover]
dotnet run --project src/Screeny -- --dev --settings --scenario hotkey-problems
dotnet run --project src/Screeny -- --dev --setup-wizard [--scenario conflicts]   # fake environment, no real actions
dotnet run --project src/Screeny -- --dev --enable-ll-hook                        # allow the Win+Shift+S hook
dotnet run --project src/Screeny -- --dev --feedback bug [--scenario <name>]     # the report window (or idea)
dotnet run --project src/Screeny -- --dev --feedback-endpoint http://localhost:5199/api/feedback   # send to a test server
```

Feedback window scenarios fill in the fields and use a fake sender, with no network: `sending` (never finishes),
`sent`, `offline`, `rate-limited`, `invalid`, `server`, `validation` (empty fields after "Send") and `details` (the
technical details list open). `not-configured` presses "Send" through the real sender instead: without
`--feedback-endpoint` that shows the "endpoint not set" state and nothing is sent or saved. The working copy always sends to `https://screeny.page/api/feedback`. `installId` in
the settings is created on the first real send. The technical details are the last 100 lines of `screeny.log` with
the profile folder replaced by `%USERPROFILE%`, other `Users\` folders and the user name by `<user>`, and the
recognized-text line of the dev text mode removed (`LogScrubber`).

Overlay scenarios: `shape`, `shape-tool`, `arrow`, `text`, `ruler`, `badge`, `guides`, `textalign`, `edgesnap`,
`flyout-beautify`, `flyout-beautify-mid`, `flyout-tool`, `flyout-shape`, `pen-bounds`, `ocr`, `ocr-select`, `qr`,
`beautify`, `beautify-sunset`, `beautify-light`, `readme-hero`, `readme-text`, `readme-beautify`.
Editor scenarios: `original`, `pin`, `select`, `guides`, `flyout-beautify`, `flyout-beautify-mid`, `flyout-tool`,
`flyout-shape`, `pen-bounds`.

Testing updates and the OCR module without GitHub:

```powershell
# a release folder made by tools/pack.ps1; --check-updates runs a manual check right away
.\portable\Screeny.exe --dev --update-source C:\tmp\rel-0.2.1 --check-updates
# a folder with a module zip made by tools/pack-ocr-module.ps1
dotnet run --project src/Screeny -- --dev --settings --module-source releases\ocr-module
dotnet run --project src/Screeny -- --dev --module-source releases\ocr-module --overlay-test shot.png --select x,y,w,h --scenario ocr
```

The `readme-*` scenarios stage the README screenshots. They expect a 1920×1080 synthetic desktop with a dashboard
window (Project Atlas Q3 report) and use these selections: `readme-hero` with `--select 360,222,872,574`,
`readme-text` with `--select 1236,378,540,600`, and `readme-beautify` with `--select 376,375,840,405`. The last one
writes the composed result with the background frame next to the source image as `<name>.export.png`. Capture the
overlay window itself (for example with `PrintWindow`), never the real screen.

When a dev instance restarts after an update, it gets the same switches, so the new version starts as a dev instance
too.

**Window size on small monitors.** Every `ChromeWindow` (settings, wizard, history, editor, update window, dialogs)
fits itself to the work area of the monitor it is actually on, not the primary one: its height is capped at that work
area minus 40 DIP (settings at most 780 DIP, the wizard 820, since both scroll), and it is moved fully inside it,
centered when it opens. This is redone when the window moves to another monitor or its DPI changes. The geometry is in
`Themes/WindowFit.cs`. To check a small monitor without one, start a dev instance with
`SCREENY_DEV_WORKAREA=x,y,w,h` (device pixels), for example `$env:SCREENY_DEV_WORKAREA = '200,100,1366,700'`.

The README screenshots need the overlay on a monitor at 100 % scale: the overlay window is the size of the test image
wherever it is, but at another scale the Screeny UI in it is scaled and the crops no longer match.

## Making a release

1. Bump `<Version>` in `src/Screeny/Screeny.csproj` and write `docs/releases/<version>.md` (see below).
2. Build:

   ```powershell
   powershell -File tools/pack.ps1 -Version 0.2.0   # or no -Version: taken from a v0.2.0 tag on the current commit
   ```

   This runs `dotnet publish` (single file, framework-dependent, win-x64) and then `vpk pack` with the id
   `DontIgnore.Screeny`. `releases/` (git-ignored) then holds `Screeny-win-Setup.exe`, `Screeny-win-Portable.zip`,
   `DontIgnore.Screeny-<version>-full.nupkg` and `releases.win.json`. You can't build the same version into the same
   folder twice: delete its files or use `-OutputDir`.
3. Publish the release by hand, after checking it:

   ```powershell
   git tag v0.2.0; git push origin v0.2.0
   gh release create v0.2.0 releases/Screeny-win-Setup.exe releases/Screeny-win-Portable.zip `
       releases/DontIgnore.Screeny-0.2.0-full.nupkg releases/releases.win.json `
       --title "Screeny 0.2.0" --notes-file docs/releases/0.2.0.md
   ```

For delta updates, put the previous release's files into `releases/` before building
(`gh release download v0.1.9 -D releases`) and attach the `*-delta.nupkg` as well. The update check reads
`releases.win.json` from the latest release that isn't a pre-release, so every release must include it.

The precise OCR module has its own releases and versions; see [tools/ocr-module/README.md](../tools/ocr-module/README.md).

## Release notes

The update window shows `docs/releases/<version>.md`, rendered from a small Markdown subset: headings, lists,
**bold** and `code`. `vpk pack --releaseNotes` stores the Markdown in `releases.win.json` as is, comments included.

Items about features that can be turned off carry a tag that GitHub doesn't render:

- `## Text and codes <!-- feature: ocr -->` covers the whole section, up to the next heading of the same or a higher
  level. The level is the number of `#`, so `####` sits inside `###`.
- `- Item <!-- feature: pins -->` covers that item.
- `<!-- feature: ocr, history -->` shows the item if any of the listed features is on.
- A tag on a line of its own applies to the next block: before a heading, to its section; before an item, to that item.
- Tags inside `` `code` `` and code blocks don't count.

Ids come from `src/Screeny/Features/FeatureCatalog.cs`. The extra id `ocr-precise` shows an item only to people who
have the precise OCR module turned on. `Release020_TagsMatchFeatures` in the tests checks the tags in `0.2.0.md`.

### Languages in release notes

One notes file carries every language. A line that holds only a language tag starts that language's section, which
runs up to the next tag:

```markdown
<!-- lang: en -->
# Screeny 0.3.0
## Text and codes <!-- feature: ocr -->
- …

<!-- lang: ru -->
# Screeny 0.3.0
## Текст и коды <!-- feature: ocr -->
- …
```

- Codes are the interface languages: `en`, `ru`, `es`, `zh-Hans`, `ja`. A tag with the exact code wins; otherwise a
  tag that serves the language counts: `zh` and `zh-CN` serve `zh-Hans`, `en-US` serves `en`. `zh-TW` and `zh-HK` serve
  nothing (Traditional Chinese readers get English).
- The update window shows the first matching section for the interface language, otherwise for English, otherwise the
  first section. Text before the first tag is shared by all languages. A file without tags is shown as is, so old
  single-language notes keep working.
- Display only: the silent-update decision below is language-independent. An update is applied silently only if it
  would be silent for every language section of every version (or the notes have no sections); otherwise Screeny asks.
- A tag counts only on a line of its own, indented less than four spaces, outside code blocks and outside multi-line
  comments. Tags inside `` `code` `` don't start a section either.
- Give every section the same headings, items and feature tags. `LanguageSections_HaveTheSameStructureAndTags` checks
  every file in `docs/releases` that has language tags.
- GitHub's release page (`gh release create --notes-file`) and `releases.win.json` get the whole file, all languages.

### When an update is applied silently

The update window hides items about features the user turned off. If nothing is left after that, an **automatic**
check doesn't ask: it downloads the update in the background and applies it when Screeny exits. A manual check always
shows the window. Screeny goes the silent way only when it knows everything that changed:

- every version between the current and the new one has release notes, and none of them is empty;
- the intermediate versions are known, which is the case when the update comes as a chain of deltas.

In any doubt it asks. On exit the silent update is skipped if update checks were turned off in the meantime, the
version was skipped, a feature from the notes was turned back on, a normal update is already scheduled, or Windows is
shutting down.

Release notes come from `releases.win.json`. Notes for intermediate versions are only available when the update
goes through a chain of deltas; otherwise only the new version's notes are known.

## Interface languages

Screeny's interface is in English, Russian, Spanish, Simplified Chinese and Japanese, the same languages as the
website. English is the fallback.

**Where strings live.** `src/Screeny/Localization/Strings.resx` holds the English text (the neutral language, see
`<NeutralLanguage>` in the csproj). `Strings.ru.resx`, `Strings.es.resx`, `Strings.zh-Hans.resx` and `Strings.ja.resx`
hold the translations and build into satellite assemblies (`ru\Screeny.resources.dll` and so on). The single-file
publish bundles them into `Screeny.exe`; `<SatelliteResourceLanguages>` in `Directory.Build.props` lists the same five
languages. Keys are dotted, grouped by surface: `Settings.*`, `Wizard.*`, `Tool.*`, `Common.*` and so on. Log messages
(`Log.*`) and texts of internal exceptions stay in the code, in English.

**Why .resx and not JSON.** The resource manager, culture fallback and satellite assemblies come with .NET, editors
such as Visual Studio and ResXManager show the languages side by side, and live switching doesn't depend on the storage
anyway: it's done by `Loc`.

**How a string reaches the screen.**

- XAML: `Text="{l:Loc Settings.Title}"` with `xmlns:l="clr-namespace:Screeny.Localization"`. `LocExtension` is a binding
  to the indexer of `Loc.Instance`, so it works in attributes, `Setter.Value`, templates and `Run.Text`.
- Code: `Loc.T("Key")`, or `Loc.F("Key", arg0, arg1)` for text with `{0}` placeholders. For an element built in code that
  must follow a language change, bind it: `Loc.Bind(element, Property, "Key")`.
- Text put together in code (counts, file names, states) is rebuilt on `Loc.Changed`. Subscribe when the window opens
  and unsubscribe in `Closed` (or `Unloaded` for a control), since `Loc.Changed` is static.

**Language setting.** `AppSettings.Language` is `"auto"` (the default, also for settings files from before this field)
or a language code. `auto` looks at `CultureInfo.CurrentUICulture` at startup, which is the Windows display language,
and walks its parents until it meets a supported code: `ru-RU` gives `ru`, `zh-CN` and `zh-SG` give `zh-Hans` through
their parent `zh-Hans`, `es-MX` gives `es`. `zh-TW` and `zh-HK` go through `zh-Hant`, which isn't supported, so they get
English, as does any other language. Thread cultures are never changed, so number and date formats stay the user's.
Settings apply the language on "Save"; the first-run wizard previews it at once, keeps it on "Apply" and reverts it on
"Skip". A settings value that isn't a supported code but is a culture name (`ru-RU`, `en-US`, `zh-CN`) is read as the
language `auto` would pick for that culture; anything else becomes `auto`.

**What switches live.** Everything that comes from XAML: the tray menu, settings, the wizard, history, editor and pin
windows, the update window, dialogs. Code-built text that is rebuilt on `Loc.Changed`: the tray tooltip, the settings
hints and precise OCR panel, the wizard heading and app cards, the history header and empty state, the editor title,
zoom and notice, the toolbar and background frame tooltips. Hotkey problems keep only their kind and error code
(`HotkeyState`, `HotkeyReport.FallbackErrorCode`) and are worded when read, so the settings hints follow the language
too. A few things keep the language they were shown in until they are shown again: validation errors and wizard results
already on screen, toasts and tray notifications, the option flyout tooltips (refreshed the next time the flyout opens)
and release notes in an open update window. After "Apply" the wizard stops following language changes; its language
list is then disabled like the other choices. Dates use the user's regional format (`CultureInfo.CurrentCulture`). The overlay is created anew for each screenshot.
Japanese and Chinese add Yu Gothic UI or Microsoft YaHei UI to the `Font.UI` resource so that WPF draws the right
glyph forms.

**Adding a string.**

1. Add the key with its English text to `Strings.resx`, and the same key to the four other files. Use the
   Russian wording as the source of meaning and keep the text short: toolbar buttons, dialog buttons and settings rows
   have little room. Spanish runs longest; check that it wraps instead of clipping.
2. Use the same placeholders `{0}`, `{1}`… in every language. Order may change, the set may not.
3. Use it from XAML or code as above. Never build a sentence from translated pieces or glue them with spaces or " · ":
   give each combination its own key, or a key with a `{0}` slot that includes the separator (`Editor.ToastNewEntry`),
   so Japanese and Chinese can drop the space. Join word lists with `Loc.ListSeparator`.
4. `dotnet test` checks that every key exists in all five languages, that no value is empty, that placeholder sets
   match, that every format string formats, and that each key the source refers to (`{l:Loc …}` and literals like
   `"Settings.Title"` whose section exists) is in `Strings.resx`.

**Adding a language.**

1. Copy `Strings.resx` to `Strings.<code>.resx` and translate every value.
2. Add the code to `Loc.Supported` and its own name to `Loc.NativeName`. If its script needs a font that Segoe UI
   doesn't cover, add one to `Loc.UiFontFamily`, and use `Loc.ListSeparator` if lists are joined differently.
3. Add the code to `<SatelliteResourceLanguages>` in `Directory.Build.props`.
4. Add a section to the release notes you ship next, and update `LocalizationTests.Supported_AreTheFiveSiteLanguages`
   and the README language lists.
5. Check the windows with the dev instance: put `"Language": "<code>"` into `settings.dev.json`, then run
   `--dev --settings`, `--dev --setup-wizard --scenario conflicts` and an `--overlay-test` with a scenario.

## Why the installer is built this way

- **Framework-dependent with `--framework net10.0-x64-desktop`**, not self-contained. The installer is about 17 MB and
  each update about 10 MB, instead of about 70 MB every time. The Velopack installer installs the runtime if it's
  missing ([bootstrapping](https://docs.velopack.io/packaging/bootstrapping)). The cost is that the portable version
  needs the .NET 10 Desktop Runtime installed already.
- **Changing the runtime (0.2.3 moved from .NET 8 to .NET 10).** The runtime is recorded in each package's nuspec
  (`runtimeDependencies`), not in `releases.win.json`. Before applying an update, `Update.exe` checks it: if the
  runtime is missing, it asks the user to install it, then runs Microsoft's installer (Windows asks for permission).
  If the user cancels, nothing is applied and the old version starts again. A silent apply (`silent: true`, used for
  updates that only touch disabled features) cancels instead of asking, so a release that changes the runtime must
  never be fully tagged: its notes have to show the normal update window.
- **Single file** stays on: `vpk` finds `Velopack.dll` inside the single-file exe and checks that it calls
  `VelopackApp.Run()`.
- **Own `Program.Main`** (`<StartupObject>Screeny.Program</StartupObject>`), because `VelopackApp.Build().Run()` has to
  run before WPF starts ([integration](https://docs.velopack.io/getting-started/csharp)). The uninstall hook
  (`OnBeforeUninstallFastCallback`) only removes the autostart entry and the downloaded modules: hooks run without
  windows and with a timeout.
- **The install folder is `%LocalAppData%\DontIgnore.Screeny`, not `%LocalAppData%\Screeny`.** Velopack wipes its own
  folder on reinstall and uninstall, and history lives in `%LocalAppData%\Screeny`. Keep them apart.
- **Autostart** points at `%LocalAppData%\DontIgnore.Screeny\Screeny.exe`, a launcher stub whose path doesn't change
  between updates. If autostart was on for an older copy (say, `publish\Screeny.exe`), the installed version rewrites
  the entry to itself on start. It never turns autostart on by itself.

`Screeny.exe` is about 29 MB. Most of that is the Windows SDK WinRT projections that the Windows OCR depends on. The
managed part of ONNX Runtime adds about 0.7 MB together with `System.Numerics.Tensors`; the native `onnxruntime.dll`
ships only in the module.

## Hotkeys

Settings format version 2 stores capture hotkeys as a list:

```json
"SettingsVersion": 2,
"Hotkeys": [
  { "Kind": "PrintScreen", "Enabled": true },
  { "Kind": "WinShiftS", "Enabled": false },
  { "Kind": "Custom", "Spec": "Ctrl+Shift+X", "Enabled": true }
]
```

An old settings file (a single `Hotkey` field) is converted on first start, and the old file is kept as
`settings.json.v1.bak`. Saving re-registers all hotkeys at once: if a newly enabled shortcut is taken, nothing changes
and the error shows under that entry.

**PrintScreen and Windows.** On Windows 11, PrintScreen opens the system snipping tool by default. In that mode
Windows lets an app register PrintScreen but never delivers the key. Screeny reads
`HKCU\Control Panel\Keyboard\PrintScreenKeyForSnippingEnabled` to detect it, shows a hint in settings, and turns on a
`Ctrl+Alt+S` fallback when no other hotkey works. The fallback goes away as soon as any configured hotkey works.

**Win+Shift+S** is reserved by Windows, so `RegisterHotKey` can't take it. The setting installs a low-level keyboard
hook (`SetWindowsHookEx(WH_KEYBOARD_LL)`) on its own thread with its own message loop. The callback only feeds a small
state machine and passes every other key through. For `Win+Shift+S` it swallows the press and release of `S`, and
injects the unassigned key `vkE8` while `Win` is still down, so releasing `Win` doesn't open the Start menu (the same
trick AutoHotkey uses). It ignores its own injected events, and a held `S` gives one screenshot. Some antivirus tools
react to keyboard hooks, which is why settings warn about it. A dev instance never installs the hook unless you pass
`--enable-ll-hook`.

## First-run wizard

The wizard reads the registry, the Startup folders, the process list and PowerToys settings, and nothing else.

| App | Detected by | Usually takes |
|---|---|---|
| Lightshot | `Lightshot.exe` process, a `Lightshot` Run value (usually HKLM, 32-bit view), a "Lightshot-…" uninstall entry | PrintScreen |
| ShareX | `ShareX.exe` process, `ShareX.lnk` in Startup | PrintScreen (+ Ctrl/Alt) |
| Greenshot | `Greenshot.exe` process, a `Greenshot` Run value or `Greenshot.lnk` | PrintScreen (+ Ctrl/Alt) |
| PicPick | `picpick.exe` process, autostart with that exe | PrintScreen (+ Ctrl/Alt/Shift) |
| Snipaste | `Snipaste.exe` process, autostart with that exe | F1, F3 (no overlap with Screeny) |
| PowerToys | only if Keyboard Manager remaps PrintScreen or Win+Shift+S | — |

Autostart entries are looked up in `HKCU\…\Run`, `HKLM\…\Run` (64- and 32-bit views) and both Startup folders. An entry
disabled in Task Manager (`Explorer\StartupApproved`) doesn't count as a conflict.

"Remove from autostart" writes the same "disabled" flag as Task Manager into
`Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\{Run|Run32|StartupFolder}` (12 bytes:
`03 00 00 00` plus a timestamp). The Run entry itself stays. For entries that need admin rights, Screeny restarts itself
through UAC with `--elevated-action disable-autostart <id>`. The elevated instance accepts only an app id from the
built-in list, never a path or command; it finds the entry again, sets the flag and exits without a window, tray icon
or hotkeys. "Uninstall" runs the app's own `UninstallString` without silent switches. "Give PrintScreen to Screeny" sets
`PrintScreenKeyForSnippingEnabled = 0`, which Windows may only pick up after signing out and back in.

The wizard version the user went through and the conflicts still present after it are stored in `settings.json`
(`SetupWizardVersion`, `KnownConflicts`).

## Precise OCR module

The app accepts only an archive whose SHA256 is built into `src/Screeny/Modules/OcrModule.cs`, along with a
`module.json` whose SHA256 is stored there too. The download goes to a temporary file that other programs can't open
until it's unpacked. After the hash check the archive is unpacked (paths that escape the folder are rejected), each
file is checked against `module.json`, and only then is the folder moved into place. Files are checked again before
every model load. Each server response and every next chunk of data has a 30-second timeout. If the module fails to
load, including when an older Visual C++ runtime from another folder is already loaded into the process, text mode
falls back to Windows OCR and settings show why.

If a new app version needs a different module, Windows OCR is used, settings show "Update module", and `Ctrl+T`
shows one tray hint. The module is never downloaded again without a click.

"Remove module" in settings deletes the folder; if the module was already used in this session, that happens after a
restart. Uninstalling Screeny removes the module folder too, even if a portable copy uses it; that copy then falls back
to Windows OCR and offers to download the module again.

## Windows OCR details

Windows OCR runs with every Windows profile language that has OCR installed and keeps the most plausible result. Small
text (words under 18 px) is often missed entirely, so Screeny also runs such areas upscaled 2× and 2.5× (bicubic) and,
for each line, keeps the reading most passes agree on. On a full 2560×1440 screenshot this takes about 0.6 s.

# Contributing to Screeny

Thanks for wanting to help. Bug reports, bug fixes, feature suggestions, translations and documentation fixes are all
welcome. You don't need to write code to be useful.

Everyone taking part is expected to follow the [Code of Conduct](CODE_OF_CONDUCT.md).

## Reporting a bug

- **A GitHub issue.** Use the [bug report form](https://github.com/DontIgnore/Screeny/issues/new?template=bug_report.yml).
  It asks for your Screeny version (Settings > About) and your Windows version.
- **In the app.** "Report a bug" is coming in 0.2.2. It is anonymous, and the maintainer reads each report before it
  becomes a public issue.
- **Security problems** go privately. See [SECURITY.md](SECURITY.md), and please don't open a public issue.

Don't include private information in reports. Blur or crop anything you wouldn't post in public.

## Suggesting an improvement

- A GitHub issue with the [feature request form](https://github.com/DontIgnore/Screeny/issues/new?template=feature_request.yml).
- "Suggest an improvement" in the app (coming in 0.2.2).
- The public roadmap at <https://screeny.page/roadmap> (coming soon), where people vote on what matters most.

## Contributing code

You need Windows and the .NET 8 SDK (the exact version is pinned in `global.json`).

```
dotnet build Screeny.sln
dotnet test tests/Screeny.Tests
dotnet run --project src/Screeny          # starts in the tray
```

[docs/maintainers.md](docs/maintainers.md) covers the dev instance (a separate copy with its own settings and test
switches), releases and how things work inside.

Some ground rules:

- Keep pull requests small and focused. For anything bigger, open an issue first so we can agree on the direction
  before you spend your time.
- Run the tests and keep the build free of warnings: `dotnet build -warnaserror`.
- Match the style of the code around you.
- UI text goes in all 5 resx files (English, Russian, Spanish, Simplified Chinese, Japanese). The tests fail if a key is
  missing in one. "Adding a string" in [docs/maintainers.md](docs/maintainers.md#interface-languages) explains how.
- Don't commit secrets, keys or personal data.
- By contributing, you agree that your work is licensed under the [GPL-3.0](LICENSE), like the rest of Screeny.

## Translations

The interface is in English, Russian, Spanish, Simplified Chinese and Japanese.

- **Improve a translation.** Edit the value in `src/Screeny/Localization/Strings.<code>.resx`
  (`ru`, `es`, `zh-Hans`, `ja`). Keep the `{0}`, `{1}` placeholders and keep the text short, since buttons and settings
  rows have little room.
- **Release notes** have one section per language, started by a line like `<!-- lang: ru -->`, in
  `docs/releases/<version>.md`.
- **Add a new language.** It's a handful of steps, listed under "Adding a language" in
  [docs/maintainers.md](docs/maintainers.md#interface-languages). If that feels like a lot, open an issue and we'll
  work it out together.

## What's out of scope

Screeny's promise is that your screenshots never leave your computer. So these won't be accepted:

- cloud upload or sharing of screenshots;
- accounts or sign-in;
- telemetry that includes screenshots or what's in them.

If you aren't sure whether an idea fits, ask in an issue first.

using Screeny.Hotkeys;

namespace Screeny.Settings;

/// <summary>Settings apply error; <see cref="Hotkey"/> — entry under which window shows text (if any).</summary>
public sealed record SettingsApplyError(string Message, HotkeyBinding? Hotkey = null);

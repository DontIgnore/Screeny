using Screeny.Hotkeys;

namespace Screeny.Settings;

/// <summary>Ошибка применения настроек; <see cref="Hotkey"/> — запись, под которой окно показывает текст (если есть).</summary>
public sealed record SettingsApplyError(string Message, HotkeyBinding? Hotkey = null);

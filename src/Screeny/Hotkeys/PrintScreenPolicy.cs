using Microsoft.Win32;

namespace Screeny.Hotkeys;

/// <summary>
/// When Windows has “Open screen snipping with PrintScreen” enabled, RegisterHotKey(VK_SNAPSHOT) succeeds,
/// but WM_HOTKEY never arrives — key taken by system. Detect by setting, not registration result.
/// </summary>
public static class PrintScreenPolicy
{
    private const int VkSnapshot = 0x2C;
    private const int FirstWindows11Build = 22000;

    public static bool IsSnippingKeyEnabled(int? registryValue, int osBuild)
        => registryValue is { } v ? v != 0 : osBuild >= FirstWindows11Build;

    public static bool IsReservedBySystem(HotkeySpec spec, bool snippingKeyEnabled)
        => snippingKeyEnabled && spec.VirtualKey == VkSnapshot && spec.Modifiers == HotkeyModifiers.None;

    public static bool IsReservedBySystem(HotkeySpec spec) => IsReservedBySystem(spec, ReadSnippingKeyEnabled());

    private static bool ReadSnippingKeyEnabled()
    {
        int? value = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard");
            if (key?.GetValue("PrintScreenKeyForSnippingEnabled") is int v) value = v;
        }
        catch (Exception) { /* no registry access — assume OS default */ }
        return IsSnippingKeyEnabled(value, Environment.OSVersion.Version.Build);
    }
}

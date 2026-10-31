using System.Text.Json.Serialization;

namespace Screeny.Hotkeys;

/// <summary>Kind of screenshot hotkey.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HotkeyKind>))]
public enum HotkeyKind
{
    /// <summary>PrintScreen without modifiers (<c>RegisterHotKey</c>).</summary>
    PrintScreen,
    /// <summary>Win+Shift+S — intercepted by low-level keyboard hook instead of system Snipping Tool.</summary>
    WinShiftS,
    /// <summary>Custom combo from <see cref="HotkeyBinding.Spec"/> (<c>RegisterHotKey</c>).</summary>
    Custom,
}

/// <summary>
/// One entry in screenshot hotkey list. In JSON: <c>{ "Kind": "Custom", "Spec": "Ctrl+Shift+X", "Enabled": true }</c>;
/// for PrintScreen and WinShiftS <c>Spec</c> field is not written.
/// </summary>
public sealed record HotkeyBinding
{
    public HotkeyKind Kind { get; init; }

    /// <summary>Combo in <see cref="HotkeyParser"/> format; only for <see cref="HotkeyKind.Custom"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Spec { get; init; }

    public bool Enabled { get; init; }

    public static HotkeyBinding PrintScreen(bool enabled) => new() { Kind = HotkeyKind.PrintScreen, Enabled = enabled };
    public static HotkeyBinding WinShiftS(bool enabled) => new() { Kind = HotkeyKind.WinShiftS, Enabled = enabled };
    public static HotkeyBinding Custom(string spec, bool enabled = true) => new() { Kind = HotkeyKind.Custom, Spec = spec, Enabled = enabled };

    /// <summary>Combo registered via <c>RegisterHotKey</c>, or null (WinShiftS, unrecognized Custom).</summary>
    public HotkeySpec? ToRegisterSpec() => Kind switch
    {
        HotkeyKind.PrintScreen => HotkeySpec.PrintScreen,
        HotkeyKind.Custom when Spec is not null && HotkeyParser.TryParse(Spec, out var spec) => spec,
        _ => null,
    };

    /// <summary>Label for user: “PrintScreen”, “Win+Shift+S” or combo.</summary>
    [JsonIgnore]
    public string DisplayName => Kind switch
    {
        HotkeyKind.PrintScreen => "PrintScreen",
        HotkeyKind.WinShiftS => "Win+Shift+S",
        _ => Spec ?? "",
    };
}

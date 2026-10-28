using System.Text.Json.Serialization;

namespace Screeny.Hotkeys;

/// <summary>Вид хоткея снимка.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<HotkeyKind>))]
public enum HotkeyKind
{
    /// <summary>Клавиша PrintScreen без модификаторов (<c>RegisterHotKey</c>).</summary>
    PrintScreen,
    /// <summary>Win+Shift+S — перехват низкоуровневым хуком клавиатуры вместо системных «Ножниц».</summary>
    WinShiftS,
    /// <summary>Своё сочетание из <see cref="HotkeyBinding.Spec"/> (<c>RegisterHotKey</c>).</summary>
    Custom,
}

/// <summary>
/// Одна запись списка хоткеев снимка. В JSON: <c>{ "Kind": "Custom", "Spec": "Ctrl+Shift+X", "Enabled": true }</c>;
/// у PrintScreen и WinShiftS поле <c>Spec</c> не пишется.
/// </summary>
public sealed record HotkeyBinding
{
    public HotkeyKind Kind { get; init; }

    /// <summary>Сочетание в формате <see cref="HotkeyParser"/>; только для <see cref="HotkeyKind.Custom"/>.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Spec { get; init; }

    public bool Enabled { get; init; }

    public static HotkeyBinding PrintScreen(bool enabled) => new() { Kind = HotkeyKind.PrintScreen, Enabled = enabled };
    public static HotkeyBinding WinShiftS(bool enabled) => new() { Kind = HotkeyKind.WinShiftS, Enabled = enabled };
    public static HotkeyBinding Custom(string spec, bool enabled = true) => new() { Kind = HotkeyKind.Custom, Spec = spec, Enabled = enabled };

    /// <summary>Сочетание, которое регистрируется через <c>RegisterHotKey</c>, или null (WinShiftS, нераспознанный Custom).</summary>
    public HotkeySpec? ToRegisterSpec() => Kind switch
    {
        HotkeyKind.PrintScreen => HotkeySpec.PrintScreen,
        HotkeyKind.Custom when Spec is not null && HotkeyParser.TryParse(Spec, out var spec) => spec,
        _ => null,
    };

    /// <summary>Подпись для пользователя: «PrintScreen», «Win+Shift+S» или сочетание.</summary>
    [JsonIgnore]
    public string DisplayName => Kind switch
    {
        HotkeyKind.PrintScreen => "PrintScreen",
        HotkeyKind.WinShiftS => "Win+Shift+S",
        _ => Spec ?? "",
    };
}

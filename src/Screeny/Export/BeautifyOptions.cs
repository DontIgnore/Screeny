using System.Windows;
using System.Windows.Media;
using Screeny.Settings;

namespace Screeny.Export;

/// <summary>Decoration background: gradients in brand palette.</summary>
public enum BeautifyPreset { Blue, Sunset, Mint, Graphite, Light, Warm }

/// <summary>Margin around screenshot: S/M/L = 24/48/80 screenshot pixels.</summary>
public enum BeautifyPadding { S, M, L }

/// <summary>
/// Decoration parameters (beautify): background gradient, margin, corner rounding, shadow.
/// Same for composition (<see cref="BeautifyComposer"/>) and swatches in decoration flyout.
/// </summary>
public sealed record BeautifyOptions(BeautifyPreset Preset, BeautifyPadding Padding, int Radius, bool Shadow)
{
    public static readonly BeautifyOptions Default = new(BeautifyPreset.Blue, BeautifyPadding.M, 8, true);

    /// <summary>Allowed roundings in screenshot pixels.</summary>
    public static readonly int[] Radii = [0, 8, 16];

    public static readonly BeautifyPreset[] Presets = Enum.GetValues<BeautifyPreset>();

    /// <summary>Margin in screenshot pixels.</summary>
    public int PaddingPx => PaddingPixels(Padding);

    public static int PaddingPixels(BeautifyPadding p) => p switch
    {
        BeautifyPadding.S => 24,
        BeautifyPadding.L => 80,
        _ => 48,
    };

    /// <summary>Shadow blur ≈ third of margin, offset down ≈ sixth, density 0.35.</summary>
    public double ShadowBlur => PaddingPx / 3.0;
    public double ShadowOffset => Math.Max(2, PaddingPx / 6.0);
    public const double ShadowOpacity = 0.35;

    /// <summary>Nearest allowed rounding (broken setting doesn't give arbitrary radius).</summary>
    public static int SnapRadius(int radius) => Radii.OrderBy(r => Math.Abs(r - radius)).ThenBy(r => r).First();

    /// <summary>Gradient start and end colors (diagonal from top-left to bottom-right).</summary>
    public static (Color From, Color To) Colors(BeautifyPreset preset) => preset switch
    {
        BeautifyPreset.Sunset => (Hex(0xFF7A59), Hex(0xFFB547)),
        BeautifyPreset.Mint => (Hex(0x2BD9A0), Hex(0x3BA7F5)),
        BeautifyPreset.Graphite => (Hex(0x2B2B33), Hex(0x4A4A57)),
        BeautifyPreset.Light => (Hex(0xF4F5F8), Hex(0xDDE3EC)),
        BeautifyPreset.Warm => (Hex(0xFFF7ED), Hex(0xFAD7B5)),
        _ => (Hex(0x3078F6), Hex(0x7C5CFF)),
    };

    public static string Name(BeautifyPreset preset) => preset switch
    {
        BeautifyPreset.Sunset => Localization.Loc.T("Beautify.Sunset"),
        BeautifyPreset.Mint => Localization.Loc.T("Beautify.Mint"),
        BeautifyPreset.Graphite => Localization.Loc.T("Beautify.Graphite"),
        BeautifyPreset.Light => Localization.Loc.T("Beautify.Light"),
        BeautifyPreset.Warm => Localization.Loc.T("Beautify.Warm"),
        _ => Localization.Loc.T("Beautify.Blue"),
    };

    /// <summary>Frozen gradient brush in relative coordinates: same on any canvas size.</summary>
    public static LinearGradientBrush Brush(BeautifyPreset preset)
    {
        var (from, to) = Colors(preset);
        var b = new LinearGradientBrush(from, to, new Point(0, 0), new Point(1, 1));
        b.Freeze();
        return b;
    }

    public static BeautifyOptions FromSettings(AppSettings s) => new(
        Enum.IsDefined(s.BeautifyPreset) ? s.BeautifyPreset : Default.Preset,
        Enum.IsDefined(s.BeautifyPadding) ? s.BeautifyPadding : Default.Padding,
        SnapRadius(s.BeautifyRadius),
        s.BeautifyShadow);

    public void SaveTo(AppSettings s)
    {
        s.BeautifyPreset = Preset;
        s.BeautifyPadding = Padding;
        s.BeautifyRadius = Radius;
        s.BeautifyShadow = Shadow;
    }

    private static Color Hex(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}

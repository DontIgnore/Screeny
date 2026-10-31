using System.Xml.Linq;
using Screeny.Themes;

namespace Screeny.Tests.Themes;

public class ThemeManagerTests
{
    [Theory]
    [InlineData(1, AppTheme.Light)]
    [InlineData(0, AppTheme.Dark)]
    [InlineData(null, AppTheme.Dark)]
    [InlineData("1", AppTheme.Dark)]
    public void RegistryValueMapsToTheme(object? value, AppTheme expected)
        => Assert.Equal(expected, ThemeManager.FromRegistryValue(value));

    [Theory]
    [InlineData("light", AppTheme.Light)]
    [InlineData(" Dark ", AppTheme.Dark)]
    public void OverrideIsParsed(string value, AppTheme expected) => Assert.Equal(expected, ThemeManager.ParseOverride(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("system")]
    public void UnknownOverrideFollowsSystem(string? value) => Assert.Null(ThemeManager.ParseOverride(value));

    /// <summary>Palettes replaced entirely: key missing in one would leave controls without color.</summary>
    [Fact]
    public void PalettesDefineSameKeys()
    {
        var dir = FindThemesDir();
        Assert.Equal(Keys(Path.Combine(dir, "Dark.xaml")), Keys(Path.Combine(dir, "Light.xaml")));
    }

    private static string[] Keys(string path)
    {
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        return XDocument.Load(path).Root!.Elements().Select(e => (string?)e.Attribute(x + "Key")).OfType<string>().Order().ToArray();
    }

    private static string FindThemesDir()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Screeny", "Themes");
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new DirectoryNotFoundException("src/Screeny/Themes not found above test output");
    }
}

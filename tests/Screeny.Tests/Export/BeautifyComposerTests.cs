using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Screeny.Export;
using Screeny.Settings;

namespace Screeny.Tests.Export;

public class BeautifyComposerTests
{
    /// <summary>Снимок с узнаваемыми пикселями: цвет зависит от координат.</summary>
    private static BitmapSource Content(int w, int h, double dpi = 96)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                px[i] = (byte)(x * 7); px[i + 1] = (byte)(y * 5); px[i + 2] = 200; px[i + 3] = 255;
            }
        return BitmapSource.Create(w, h, dpi, dpi, PixelFormats.Bgra32, null, px, w * 4);
    }

    private static byte[] Pixels(BitmapSource b)
    {
        var src = new FormatConvertedBitmap(b, PixelFormats.Bgra32, null, 0);
        var px = new byte[b.PixelWidth * b.PixelHeight * 4];
        src.CopyPixels(px, b.PixelWidth * 4, 0);
        return px;
    }

    private static Color At(byte[] px, int width, int x, int y)
    {
        int i = (y * width + x) * 4;
        return Color.FromArgb(px[i + 3], px[i + 2], px[i + 1], px[i]);
    }

    private static void Near(Color expected, Color actual, int tolerance = 12)
    {
        Assert.True(Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance
            && Math.Abs(expected.B - actual.B) <= tolerance && expected.A == actual.A, $"expected {expected}, actual {actual}");
    }

    [Theory]
    [InlineData(BeautifyPadding.S, 24)]
    [InlineData(BeautifyPadding.M, 48)]
    [InlineData(BeautifyPadding.L, 80)]
    public void Size_IsContentPlusTwicePadding_At96Dpi(BeautifyPadding padding, int px)
    {
        var o = BeautifyOptions.Default with { Padding = padding };
        var result = BeautifyComposer.Compose(Content(120, 70), o);
        Assert.Equal(120 + 2 * px, result.PixelWidth);
        Assert.Equal(70 + 2 * px, result.PixelHeight);
        Assert.Equal(96, result.DpiX, 3);
        Assert.Equal(96, result.DpiY, 3);
        Assert.True(result.IsFrozen);
        Assert.Equal((120 + 2 * px, 70 + 2 * px), BeautifyComposer.SizeFor(120, 70, o));
    }

    [Theory]
    [InlineData(BeautifyPreset.Blue, 16, true)]
    [InlineData(BeautifyPreset.Light, 16, true)]
    [InlineData(BeautifyPreset.Graphite, 8, false)]
    [InlineData(BeautifyPreset.Warm, 0, true)]
    public void EveryPixel_IsOpaque_IncludingRoundedCornersAndShadow(BeautifyPreset preset, int radius, bool shadow)
    {
        var o = new BeautifyOptions(preset, BeautifyPadding.M, radius, shadow);
        var result = BeautifyComposer.Compose(Content(100, 60), o);
        var px = Pixels(result);
        for (int i = 3; i < px.Length; i += 4) Assert.Equal(255, px[i]);
    }

    [Fact]
    public void Content_IsPlacedAtPaddingOffset_Unchanged()
    {
        var content = Content(100, 60);
        var src = Pixels(content);
        var o = new BeautifyOptions(BeautifyPreset.Mint, BeautifyPadding.S, 16, true);
        var result = BeautifyComposer.Compose(content, o);
        var px = Pixels(result);
        // Вдали от скруглённых углов каждый пиксель снимка — ровно на месте со сдвигом на отступ.
        for (int y = 16; y < 60 - 16; y++)
            for (int x = 0; x < 100; x++)
                Assert.Equal(At(src, 100, x, y), At(px, result.PixelWidth, x + 24, y + 24));
        for (int y = 0; y < 60; y++)
            for (int x = 16; x < 100 - 16; x++)
                Assert.Equal(At(src, 100, x, y), At(px, result.PixelWidth, x + 24, y + 24));
    }

    [Fact]
    public void ContentWithOtherDpi_IsNotRescaled()
    {
        var content = Content(100, 60, dpi: 144);
        var result = BeautifyComposer.Compose(content, new BeautifyOptions(BeautifyPreset.Blue, BeautifyPadding.S, 0, false));
        Assert.Equal(100 + 48, result.PixelWidth);
        Assert.Equal(At(Pixels(content), 100, 99, 59), At(Pixels(result), result.PixelWidth, 24 + 99, 24 + 59));
    }

    [Fact]
    public void RoundedCorner_ShowsBackground_NotContent_AndNotBlack()
    {
        var o = new BeautifyOptions(BeautifyPreset.Light, BeautifyPadding.M, 16, false);
        var result = BeautifyComposer.Compose(Content(100, 60), o);
        var px = Pixels(result);
        var corner = At(px, result.PixelWidth, 48, 48); // левый верхний пиксель снимка срезан скруглением
        var from = BeautifyOptions.Colors(BeautifyPreset.Light).From;
        Assert.True(corner.R > 200 && corner.G > 200 && corner.B > 200, $"corner {corner} must be the light background");
        Assert.NotEqual(At(Pixels(Content(100, 60)), 100, 0, 0), corner);
        Near(from, At(px, result.PixelWidth, 0, 0)); // угол холста — начало градиента
    }

    [Theory]
    [InlineData(BeautifyPreset.Blue)]
    [InlineData(BeautifyPreset.Sunset)]
    [InlineData(BeautifyPreset.Mint)]
    [InlineData(BeautifyPreset.Graphite)]
    [InlineData(BeautifyPreset.Light)]
    [InlineData(BeautifyPreset.Warm)]
    public void Background_IsDiagonalGradientOfPreset(BeautifyPreset preset)
    {
        var result = BeautifyComposer.Compose(Content(40, 40), new BeautifyOptions(preset, BeautifyPadding.L, 8, true));
        var px = Pixels(result);
        int w = result.PixelWidth, h = result.PixelHeight;
        var (from, to) = BeautifyOptions.Colors(preset);
        Near(from, At(px, w, 0, 0));
        Near(to, At(px, w, w - 1, h - 1));
    }

    [Fact]
    public void Shadow_DarkensBelowContent_Only()
    {
        var with = new BeautifyOptions(BeautifyPreset.Light, BeautifyPadding.M, 0, true);
        var a = Pixels(BeautifyComposer.Compose(Content(100, 60), with));
        var b = Pixels(BeautifyComposer.Compose(Content(100, 60), with with { Shadow = false }));
        int w = 100 + 96;
        // Сразу под снимком тень есть, далеко в углу холста — нет.
        Assert.True(At(a, w, 48 + 50, 48 + 60 + 4).R < At(b, w, 48 + 50, 48 + 60 + 4).R - 10);
        Assert.Equal(At(b, w, 2, 2), At(a, w, 2, 2));
    }

    [Fact]
    public void ShadowAndCorners_OnLightBackground_StayLight_NoBlackPixels()
    {
        // Тень и углы поверх светлого фона, а не поверх пустоты: при DIB-вставке не должно быть чёрного.
        var o = new BeautifyOptions(BeautifyPreset.Light, BeautifyPadding.M, 16, true);
        var result = BeautifyComposer.Compose(Content(100, 60), o);
        var px = Pixels(result);
        int w = result.PixelWidth, h = result.PixelHeight, p = o.PaddingPx;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool inContent = x >= p && x < p + 100 && y >= p && y < p + 60;
                if (inContent) continue;
                var c = At(px, w, x, y);
                Assert.True(c.R > 120 && c.G > 120 && c.B > 120, $"({x},{y}) = {c}");
            }
        // Срезанный скруглением угол снимка — тоже фон.
        var corner = At(px, w, p, p);
        Assert.True(corner.R > 120, $"corner {corner}");
    }

    [Fact]
    public void Radius_LargerThanHalfContent_IsClamped()
    {
        var result = BeautifyComposer.Compose(Content(10, 10), new BeautifyOptions(BeautifyPreset.Blue, BeautifyPadding.S, 16, true));
        Assert.Equal(58, result.PixelWidth);
        var px = Pixels(result);
        for (int i = 3; i < px.Length; i += 4) Assert.Equal(255, px[i]);
    }
}

public class BeautifySettingsTests
{
    [Fact]
    public void Defaults_AreOffWithMediumBlue()
    {
        var s = new AppSettings();
        Assert.False(s.BeautifyEnabled);
        Assert.Equal(BeautifyOptions.Default, BeautifyOptions.FromSettings(s));
    }

    [Theory]
    [InlineData(-5, 0)]
    [InlineData(3, 0)]
    [InlineData(5, 8)]
    [InlineData(12, 8)]
    [InlineData(13, 16)]
    [InlineData(400, 16)]
    public void Normalize_SnapsRadius(int radius, int expected)
        => Assert.Equal(expected, new AppSettings { BeautifyRadius = radius }.Normalize().BeautifyRadius);

    [Fact]
    public void Normalize_ReplacesUnknownEnums()
    {
        var s = new AppSettings { BeautifyPreset = (BeautifyPreset)42, BeautifyPadding = (BeautifyPadding)(-1) }.Normalize();
        Assert.Equal(BeautifyOptions.Default.Preset, s.BeautifyPreset);
        Assert.Equal(BeautifyOptions.Default.Padding, s.BeautifyPadding);
    }

    [Fact]
    public void Options_RoundTripThroughSettings()
    {
        var o = new BeautifyOptions(BeautifyPreset.Warm, BeautifyPadding.L, 16, false);
        var s = new AppSettings();
        o.SaveTo(s);
        Assert.Equal(o, BeautifyOptions.FromSettings(s));
    }
}

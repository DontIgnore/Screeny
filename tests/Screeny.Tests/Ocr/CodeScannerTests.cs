using System.Windows;
using Screeny.Ocr;
using ZXing;
using ZXing.Common;

namespace Screeny.Tests.Ocr;

public class CodeScannerTests
{
    /// <summary>Белый холст Bgra32 с нарисованными на нём кодами.</summary>
    private sealed class Canvas
    {
        public readonly int W, H;
        public readonly byte[] Px;
        public Canvas(int w, int h) { W = w; H = h; Px = new byte[w * h * 4]; Array.Fill(Px, (byte)255); }

        /// <summary>Рисует код и возвращает его прямоугольник на холсте.</summary>
        public Rect Draw(BarcodeFormat format, string text, int x, int y, int w, int h, int margin = 4)
        {
            var writer = new BarcodeWriterPixelData
            {
                Format = format,
                Options = new EncodingOptions { Width = w, Height = h, Margin = margin, PureBarcode = true },
            };
            var data = writer.Write(text);
            for (int row = 0; row < data.Height; row++)
                Buffer.BlockCopy(data.Pixels, row * data.Width * 4, Px, ((y + row) * W + x) * 4, data.Width * 4);
            return new Rect(x, y, data.Width, data.Height);
        }

        public void Invert()
        {
            for (int i = 0; i < Px.Length; i += 4) { Px[i] = (byte)(255 - Px[i]); Px[i + 1] = (byte)(255 - Px[i + 1]); Px[i + 2] = (byte)(255 - Px[i + 2]); }
        }

        /// <summary>Поворот на 90° по часовой.</summary>
        public Canvas Rotate90()
        {
            var r = new Canvas(H, W);
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                    Buffer.BlockCopy(Px, (y * W + x) * 4, r.Px, (x * r.W + (H - 1 - y)) * 4, 4);
            return r;
        }

        public IReadOnlyList<DetectedCode> Scan() => CodeScanner.Scan(Px, W, H);
    }

    private static Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);

    [Fact]
    public void SingleQr_IsFound_WithBoxAroundIt()
    {
        var c = new Canvas(400, 300);
        var drawn = c.Draw(BarcodeFormat.QR_CODE, "https://example.com/screeny", 100, 50, 180, 180);
        var code = Assert.Single(c.Scan());
        Assert.Equal("https://example.com/screeny", code.Text);
        Assert.Equal(BarcodeFormat.QR_CODE, code.Format);
        Assert.True(code.Box.Contains(Center(drawn)));
        Assert.True(code.Box.Width > drawn.Width * 0.6 && code.Box.Width < drawn.Width * 1.3, $"box {code.Box} vs drawn {drawn}");
        Assert.True(code.IsOpenableUrl);
    }

    [Fact]
    public void TwoQrCodes_AreBothFound_InReadingOrder()
    {
        var c = new Canvas(600, 300);
        var left = c.Draw(BarcodeFormat.QR_CODE, "first", 30, 60, 180, 180);
        var right = c.Draw(BarcodeFormat.QR_CODE, "second code", 360, 60, 180, 180);
        var codes = c.Scan();
        Assert.Equal(["first", "second code"], codes.Select(x => x.Text));
        Assert.True(codes[0].Box.Contains(Center(left)));
        Assert.True(codes[1].Box.Contains(Center(right)));
    }

    [Fact]
    public void InvertedQr_LightOnDark_IsFound()
    {
        var c = new Canvas(300, 300);
        c.Draw(BarcodeFormat.QR_CODE, "inverted", 50, 50, 200, 200);
        c.Invert();
        Assert.Equal("inverted", Assert.Single(c.Scan()).Text);
    }

    [Fact]
    public void QrRotated90_IsFound()
    {
        var c = new Canvas(400, 300);
        c.Draw(BarcodeFormat.QR_CODE, "rotated", 120, 40, 200, 200);
        var rotated = c.Rotate90();
        var code = Assert.Single(rotated.Scan());
        Assert.Equal("rotated", code.Text);
        // Код на повёрнутом холсте: x' = H-1-y, y' = x → центр (300-1-140, 220).
        Assert.True(code.Box.Contains(new Point(299 - 140, 220)), $"box {code.Box}");
    }

    [Fact]
    public void Ean13_IsFound_WithNonEmptyBox()
    {
        var c = new Canvas(500, 260);
        var drawn = c.Draw(BarcodeFormat.EAN_13, "5901234123457", 60, 60, 300, 120, margin: 10);
        var code = Assert.Single(c.Scan());
        Assert.Equal("5901234123457", code.Text);
        Assert.Equal(BarcodeFormat.EAN_13, code.Format);
        Assert.Equal("EAN-13", code.FormatName);
        // Рамка по высоте штрихов, а не по строке сканирования: почти вся высота кода и не дальше его краёв.
        Assert.True(code.Box.Height > drawn.Height * 0.8, $"box {code.Box} vs drawn {drawn}");
        var around = drawn; around.Inflate(8, 8);
        Assert.True(around.Contains(code.Box), $"box {code.Box} vs drawn {drawn}");
        Assert.True(code.Box.Contains(Center(drawn)), $"box {code.Box} vs drawn {drawn}");
        Assert.False(code.IsOpenableUrl);
    }

    [Fact]
    public void Ean13Rotated90_IsFound_WithBoxOnTheBars()
    {
        var c = new Canvas(500, 260);
        c.Draw(BarcodeFormat.EAN_13, "5901234123457", 60, 60, 300, 120, margin: 10);
        var code = Assert.Single(c.Rotate90().Scan());
        Assert.Equal("5901234123457", code.Text);
        // Центр кода (210, 120) на повёрнутом холсте: (260-1-120, 210).
        Assert.True(code.Box.Contains(new Point(139, 210)), $"box {code.Box}");
        Assert.True(code.Box.Width > 120 * 0.8, $"box {code.Box}");
    }

    [Fact]
    public void QrAndBarcode_Together()
    {
        var c = new Canvas(700, 300);
        c.Draw(BarcodeFormat.QR_CODE, "qr", 30, 40, 200, 200);
        c.Draw(BarcodeFormat.EAN_13, "5901234123457", 300, 80, 300, 120, margin: 10);
        Assert.Equal(["qr", "5901234123457"], c.Scan().Select(x => x.Text).Order(StringComparer.Ordinal).Reverse());
    }

    [Fact]
    public void EmptyImage_HasNoCodes()
    {
        Assert.Empty(new Canvas(300, 200).Scan());
        Assert.Empty(CodeScanner.Scan([], 0, 0));
    }

    [Theory]
    [InlineData("http://example.com", true)]
    [InlineData("https://example.com/path?q=1#x", true)]
    [InlineData("  HTTPS://EXAMPLE.COM  ", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/System32/calc.exe", false)]
    [InlineData("ms-settings:privacy", false)]
    [InlineData("mailto:a@b.c", false)]
    [InlineData("example.com", false)]
    [InlineData("http://", false)]
    [InlineData("https://exa\nmple.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsOpenableUrl_OnlyHttpAndHttps(string? text, bool expected) => Assert.Equal(expected, CodeScanner.IsOpenableUrl(text));
}

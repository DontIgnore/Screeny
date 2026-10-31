using Screeny.Annotations;

namespace Screeny.Tests.Annotations;

public class PixelOpsTests
{
    private static byte[] Checker(int w, int h)
    {
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            byte v = (byte)(((x + y) % 2 == 0) ? 0 : 200);
            int i = (y * w + x) * 4; px[i] = v; px[i + 1] = v; px[i + 2] = v; px[i + 3] = 255;
        }
        return px;
    }

    [Fact]
    public void Pixelate_AveragesEachBlock()
    {
        var px = Checker(4, 4);
        PixelOps.Pixelate(px, 4, 4, blockSize: 2);
        for (int i = 0; i < px.Length; i += 4) { Assert.Equal(100, px[i]); Assert.Equal(255, px[i + 3]); }
    }

    [Fact]
    public void Pixelate_HandlesPartialBlocksAtEdge()
    {
        var px = Checker(5, 3);
        PixelOps.Pixelate(px, 5, 3, blockSize: 4);
        // Right column (x=4) — 1×3 block: values 0,200,0 → 67
        int i = (0 * 5 + 4) * 4; Assert.Equal(67, px[i]);
    }

    [Fact]
    public void BoxBlur_UniformImageStaysUniform()
    {
        var px = new byte[6 * 6 * 4]; Array.Fill(px, (byte)77);
        PixelOps.BoxBlur(px, 6, 6, radius: 2);
        Assert.All(px, b => Assert.Equal(77, b));
    }

    [Fact]
    public void BoxBlur_SmoothsEdge()
    {
        var px = new byte[4 * 1 * 4];
        for (int x = 0; x < 4; x++) { int i = x * 4; byte v = (byte)(x < 2 ? 0 : 255); px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255; }
        PixelOps.BoxBlur(px, 4, 1, radius: 1);
        Assert.True(px[1 * 4] > 0 && px[1 * 4] < 255);
        Assert.True(px[2 * 4] > 0 && px[2 * 4] < 255);
    }

    [Fact]
    public void BoxBlur_VerticalPass_SmoothsHorizontalEdge()
    {
        // 1×4 column: top black, bottom white — horizontal pass changes nothing, only vertical works.
        var px = new byte[1 * 4 * 4];
        for (int y = 0; y < 4; y++) { int i = y * 4; byte v = (byte)(y < 2 ? 0 : 255); px[i] = px[i + 1] = px[i + 2] = v; px[i + 3] = 255; }
        PixelOps.BoxBlur(px, 1, 4, radius: 1);
        Assert.Equal(0, px[0]);
        Assert.Equal(85, px[1 * 4]);   // (0 + 0 + 255) / 3
        Assert.Equal(170, px[2 * 4]);  // (0 + 255 + 255) / 3
        Assert.Equal(255, px[3 * 4]);
        Assert.Equal(255, px[1 * 4 + 3]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(40)] // window wider than image
    public void BoxBlur_MatchesNaiveReference(int radius)
    {
        int w = 64, h = 48;
        var rnd = new Random(12345 + radius);
        var px = new byte[w * h * 4]; rnd.NextBytes(px);
        var expected = (byte[])px.Clone();
        NaiveBoxBlur(expected, w, h, radius);

        PixelOps.BoxBlur(px, w, h, radius);

        for (int i = 0; i < px.Length; i++)
            Assert.True(Math.Abs(px[i] - expected[i]) <= 1, $"byte {i}: {px[i]} vs {expected[i]}");
    }

    [Fact]
    public void BoxBlur_WithScratch_ReusesBufferAndMatches()
    {
        int w = 32, h = 16;
        var rnd = new Random(7);
        var a = new byte[w * h * 4]; rnd.NextBytes(a);
        var b = (byte[])a.Clone();
        var scratch = new byte[w * h * 4 + 100]; // buffer may be longer than needed
        PixelOps.BoxBlur(a, w, h, 3);
        PixelOps.BoxBlur(b, w, h, 3, scratch);
        Assert.Equal(a, b);
    }

    /// <summary>Previous O(w·h·r) implementation — reference.</summary>
    private static void NaiveBoxBlur(byte[] px, int width, int height, int radius)
    {
        var tmp = new byte[px.Length];
        NaivePass(px, tmp, width, height, radius, true);
        NaivePass(tmp, px, width, height, radius, false);
    }

    private static void NaivePass(byte[] src, byte[] dst, int w, int h, int r, bool horizontal)
    {
        int n = 2 * r + 1;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++)
        {
            int sb = 0, sg = 0, sr = 0, sa = 0;
            for (int k = -r; k <= r; k++)
            {
                int xx = horizontal ? Math.Clamp(x + k, 0, w - 1) : x;
                int yy = horizontal ? y : Math.Clamp(y + k, 0, h - 1);
                int i = (yy * w + xx) * 4; sb += src[i]; sg += src[i + 1]; sr += src[i + 2]; sa += src[i + 3];
            }
            int o = (y * w + x) * 4;
            dst[o] = (byte)(sb / n); dst[o + 1] = (byte)(sg / n); dst[o + 2] = (byte)(sr / n); dst[o + 3] = (byte)(sa / n);
        }
    }
}

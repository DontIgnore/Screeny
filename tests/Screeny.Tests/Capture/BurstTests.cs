using Screeny.Capture;

namespace Screeny.Tests.Capture;

public class BurstFrameBufferTests
{
    private static byte[] Frame(byte value, int size = 16) => Enumerable.Repeat(value, size).ToArray();

    [Fact]
    public void IdenticalToPrevious_IsDropped()
    {
        var b = new BurstFrameBuffer();
        Assert.Equal(BurstOffer.Added, b.Offer(Frame(1)));
        Assert.Equal(BurstOffer.Duplicate, b.Offer(Frame(1)));
        Assert.Equal(BurstOffer.Added, b.Offer(Frame(2)));
        Assert.Equal(BurstOffer.Added, b.Offer(Frame(1))); // сравнение только с предыдущим принятым
        Assert.Equal((3, 1, 48L), (b.Count, b.Duplicates, b.Bytes));
    }

    [Fact]
    public void OneChangedByte_IsANewFrame()
    {
        // Маленькая подсказка меняет несколько пикселей: такой кадр не теряется.
        var b = new BurstFrameBuffer();
        var a = Frame(0, 4096);
        var c = (byte[])a.Clone();
        c[2049] = 1;
        b.Offer(a);
        Assert.Equal(BurstOffer.Added, b.Offer(c));
    }

    [Fact]
    public void ByteCap_StopsTheSeries()
    {
        var b = new BurstFrameBuffer(maxBytes: 40, maxFrames: 100);
        Assert.Equal(BurstOffer.Added, b.Offer(Frame(1)));
        Assert.Equal(BurstOffer.Added, b.Offer(Frame(2)));
        Assert.Equal(BurstOffer.Full, b.Offer(Frame(3))); // 48 > 40
        Assert.True(b.HitLimit);
        Assert.Equal((2, 32L), (b.Count, b.Bytes));
    }

    [Fact]
    public void FrameCap_StopsTheSeries()
    {
        var b = new BurstFrameBuffer(maxBytes: long.MaxValue, maxFrames: 3);
        for (byte i = 0; i < 3; i++) Assert.Equal(BurstOffer.Added, b.Offer(Frame(i)));
        Assert.Equal(BurstOffer.Full, b.Offer(Frame(9)));
        Assert.Equal(3, b.Count);
    }

    [Fact]
    public void DuplicateOfLast_WhenFull_IsStillJustADuplicate()
    {
        var b = new BurstFrameBuffer(maxBytes: 16, maxFrames: 10);
        b.Offer(Frame(1));
        Assert.Equal(BurstOffer.Duplicate, b.Offer(Frame(1)));
        Assert.False(b.HitLimit);
    }

    [Fact]
    public void DefaultCaps_Are600MbAnd60Frames()
    {
        var b = new BurstFrameBuffer();
        Assert.Equal(600L * 1024 * 1024, b.MaxBytes);
        Assert.Equal(60, b.MaxFrames);
        // Кадр 4K — 33 МБ: в 600 МБ помещается 18 таких кадров.
        long frame4K = 3840L * 2160 * 4;
        Assert.Equal(18, (int)(b.MaxBytes / frame4K));
    }
}

public class BurstTimelineTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void ReleasedBeforeThreshold_IsATap()
    {
        var t = new BurstTimeline();
        Assert.Equal(BurstStep.Wait, t.Step(Ms(5), keyDown: true));
        Assert.Equal(BurstStep.Wait, t.Step(Ms(120), keyDown: true));
        Assert.Equal(BurstStep.OpenTap, t.Step(Ms(125), keyDown: false));
        Assert.False(t.IsBursting);
    }

    [Fact]
    public void KeyAlreadyUpOnFirstPoll_IsATap()
        => Assert.Equal(BurstStep.OpenTap, new BurstTimeline().Step(Ms(1), keyDown: false));

    [Fact]
    public void HeldPastThreshold_CapturesAboutTenPerSecondUntilRelease()
    {
        var t = new BurstTimeline();
        var captures = new List<double>();
        for (double ms = 0; ms <= 800; ms += 5)
            if (t.Step(Ms(ms), keyDown: true) == BurstStep.Capture) captures.Add(ms);
        Assert.Equal([250, 350, 450, 550, 650, 750], captures);
        Assert.True(t.IsBursting);
        Assert.Equal(BurstStep.OpenBurst, t.Step(Ms(805), keyDown: false));
    }

    [Fact]
    public void ThresholdIsInclusive()
    {
        var t = new BurstTimeline();
        Assert.Equal(BurstStep.Wait, t.Step(Ms(249), keyDown: true));
        Assert.Equal(BurstStep.Capture, t.Step(Ms(250), keyDown: true));
    }

    [Fact]
    public void WhenFull_StopsCapturingButWaitsForRelease()
    {
        var t = new BurstTimeline();
        Assert.Equal(BurstStep.Capture, t.Step(Ms(300), keyDown: true));
        Assert.Equal(BurstStep.Wait, t.Step(Ms(500), keyDown: true, full: true));
        Assert.Equal(BurstStep.Wait, t.Step(Ms(2000), keyDown: true, full: true));
        Assert.Equal(BurstStep.OpenBurst, t.Step(Ms(2005), keyDown: false, full: true));
    }

    [Fact]
    public void StuckKey_OpensAfterMaxHold()
    {
        var t = new BurstTimeline();
        t.Step(Ms(300), keyDown: true);
        Assert.Equal(BurstStep.OpenBurst, t.Step(BurstTimeline.MaxHold, keyDown: true, full: true));
    }

    [Fact]
    public void SlowCapture_DoesNotQueueUpFrames()
    {
        // Кадр снимался 300 мс: следующий — сразу, но один, без догоняющей пачки.
        var t = new BurstTimeline();
        Assert.Equal(BurstStep.Capture, t.Step(Ms(250), keyDown: true));
        Assert.Equal(BurstStep.Capture, t.Step(Ms(550), keyDown: true));
        Assert.Equal(BurstStep.Wait, t.Step(Ms(555), keyDown: true));
    }
}

public class BurstThumbnailTests
{
    [Theory]
    [InlineData(1920, 1080, 192, 108)]
    [InlineData(5120, 1440, 192, 54)]   // два монитора рядом: ограничивает ширина
    [InlineData(1080, 1920, 61, 108)]   // портретный: ограничивает высота
    public void Thumbnail_FitsTheStrip(int w, int h, int tw, int th)
    {
        var px = new byte[w * h * 4];
        var t = BurstRecorder.Thumbnail(px, w, h);
        Assert.Equal((tw, th), (t.PixelWidth, t.PixelHeight));
        Assert.True(t.IsFrozen);
    }

    [Fact]
    public void Thumbnail_SamplesThePicture()
    {
        // Левая половина белая, правая чёрная: у миниатюры так же.
        const int w = 400, h = 200;
        var px = new byte[w * h * 4];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w / 2; x++)
                for (int c = 0; c < 4; c++) px[(y * w + x) * 4 + c] = 255;
        var t = BurstRecorder.Thumbnail(px, w, h);
        var small = new byte[t.PixelWidth * t.PixelHeight * 4];
        t.CopyPixels(small, t.PixelWidth * 4, 0);
        Assert.Equal(255, small[0]);
        Assert.Equal(0, small[(t.PixelWidth - 1) * 4]);
    }
}

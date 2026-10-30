namespace Screeny.Capture;

/// <summary>Что стало с кадром серии в <see cref="BurstFrameBuffer.Offer"/>.</summary>
public enum BurstOffer
{
    /// <summary>Кадр новый, место есть: его надо сохранить.</summary>
    Added,
    /// <summary>Пиксель в пиксель как предыдущий принятый: не нужен.</summary>
    Duplicate,
    /// <summary>Лимит кадров или байтов исчерпан: серия дальше не снимается.</summary>
    Full,
}

/// <summary>
/// Учёт кадров серии (удержание клавиши снимка): отбрасывает кадр, совпадающий с предыдущим, и держит общий объём в
/// пределах <see cref="MaxBytes"/> и <see cref="MaxFrames"/>. Сами картинки хранит вызывающий; здесь — только копия
/// последнего принятого кадра для сравнения. Чистая логика.
/// <para>
/// Сравнение точное (<c>SequenceEqual</c> по байтам, векторное, порядка нескольких миллисекунд на 4K), а не по
/// выборке: появившаяся маленькая подсказка — ровно то, ради чего серию и снимают, и разреженная выборка могла бы её
/// пропустить.
/// </para>
/// </summary>
public sealed class BurstFrameBuffer(long maxBytes = BurstFrameBuffer.DefaultMaxBytes, int maxFrames = BurstFrameBuffer.DefaultMaxFrames)
{
    /// <summary>Потолок памяти на всю серию, включая первый кадр.</summary>
    public const long DefaultMaxBytes = 600L * 1024 * 1024;
    /// <summary>Потолок числа кадров (≈6 с при 10 кадрах в секунду).</summary>
    public const int DefaultMaxFrames = 60;

    private byte[]? _last;

    public long MaxBytes { get; } = maxBytes;
    public int MaxFrames { get; } = maxFrames;
    /// <summary>Принято кадров.</summary>
    public int Count { get; private set; }
    /// <summary>Байтов в принятых кадрах.</summary>
    public long Bytes { get; private set; }
    /// <summary>Отброшено одинаковых кадров.</summary>
    public int Duplicates { get; private set; }
    /// <summary>Был отказ по лимиту.</summary>
    public bool HitLimit { get; private set; }

    /// <summary>Решить судьбу кадра. Принятый запоминается как образец для следующего сравнения.</summary>
    public BurstOffer Offer(ReadOnlySpan<byte> pixels)
    {
        if (_last is not null && pixels.SequenceEqual(_last)) { Duplicates++; return BurstOffer.Duplicate; }
        if (Count >= MaxFrames || Bytes + pixels.Length > MaxBytes) { HitLimit = true; return BurstOffer.Full; }
        if (_last is null || _last.Length != pixels.Length) _last = new byte[pixels.Length];
        pixels.CopyTo(_last);
        Count++;
        Bytes += pixels.Length;
        return BurstOffer.Added;
    }

    /// <summary>Отпустить образец для сравнения (серия закончилась).</summary>
    public void ReleaseLast() => _last = null;
}

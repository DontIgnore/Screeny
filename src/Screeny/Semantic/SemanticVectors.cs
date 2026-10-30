using System.Security.Cryptography;
using System.Text;

namespace Screeny.Semantic;

/// <summary>Чем посчитаны векторы: модель, её версия и бюджет картинки. Не совпадает с текущей — снимок индексируется заново.</summary>
public sealed record SemanticMarker(string ModelId, int ModelVersion, int SoftTokens);

/// <summary>
/// Векторы снимка истории для смыслового поиска — sidecar <c>&lt;stamp&gt;.vec</c> (двоичный, ≈3 КБ): вектор картинки и
/// вектор распознанного текста (<see cref="Screeny.History.HistoryText"/>; нет текста — нет и вектора), по 768 чисел fp16,
/// L2-нормированные. Заголовок: <c>SVEC</c>, версия формата, <see cref="SemanticMarker"/> и отпечаток текста, по которому
/// посчитан текстовый вектор: текст распознали заново (сменились языки OCR) — пересчитывается только он, картинка остаётся.
/// Вектор считается только по сохранённому снимку с пометками, никогда по <c>.orig.png</c>: размытое не должно попасть в индекс.
/// </summary>
public sealed record SemanticVectors(SemanticMarker Marker, ulong TextHash, float[]? Image, float[]? Text)
{
    public const int FormatVersion = 1;
    private static readonly byte[] Magic = "SVEC"u8.ToArray();

    /// <summary>Меньше букв и цифр — не текст, а шум распознавания (на фотографии «, ' зуз•»).</summary>
    public const int MinTextChars = 12;

    /// <summary>
    /// Текст, по которому считается текстовый вектор, или null. Короткий мусор распознавания не индексируется: вектор такой
    /// строки близок ко многим запросам и вытаскивал бы фотографию наверх по «график» и «счёт».
    /// </summary>
    public static string? UsableText(string? text)
        => text is not null && text.Count(char.IsLetterOrDigit) >= MinTextChars ? text : null;

    /// <summary>Отпечаток распознанного текста; пустой текст — 0 (текстового вектора нет).</summary>
    public static ulong HashText(string? text)
        => string.IsNullOrWhiteSpace(text) ? 0 : BitConverter.ToUInt64(SHA256.HashData(Encoding.UTF8.GetBytes(text)), 0) | 1;

    /// <summary>Наибольшее сходство запроса с картинкой или текстом снимка (векторы нормированы — скалярное произведение).</summary>
    public double Score(float[] query)
    {
        double best = double.NegativeInfinity;
        if (Image is not null) best = Dot(Image, query);
        if (Text is not null) best = Math.Max(best, Dot(Text, query));
        return best;
    }

    public static double Dot(float[] a, float[] b)
    {
        double s = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) s += (double)a[i] * b[i];
        return s;
    }

    public static float[] Normalize(float[] v)
    {
        double s = 0;
        foreach (var x in v) s += (double)x * x;
        if (s <= 0) return v;
        float inv = (float)(1.0 / Math.Sqrt(s));
        for (int i = 0; i < v.Length; i++) v[i] *= inv;
        return v;
    }

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(Magic);
            w.Write(FormatVersion);
            w.Write(Marker.ModelId);
            w.Write(Marker.ModelVersion);
            w.Write(Marker.SoftTokens);
            w.Write(TextHash);
            int dims = Image?.Length ?? Text?.Length ?? 0;
            w.Write(dims);
            w.Write((byte)((Image is not null ? 1 : 0) | (Text is not null ? 2 : 0)));
            foreach (var v in new[] { Image, Text })
                if (v is not null)
                    foreach (var x in v) w.Write((Half)x);
        }
        return ms.ToArray();
    }

    /// <summary>Разбор файла; null — битый, чужой или формат новее поддерживаемого (снимок индексируется заново).</summary>
    public static SemanticVectors? TryParse(byte[] data)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(data, writable: false), Encoding.UTF8);
            if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic) || r.ReadInt32() != FormatVersion) return null;
            var marker = new SemanticMarker(r.ReadString(), r.ReadInt32(), r.ReadInt32());
            ulong hash = r.ReadUInt64();
            int dims = r.ReadInt32();
            byte flags = r.ReadByte();
            if (dims is <= 0 or > 4096 || flags is 0 or > 3) return null;
            float[]? Read()
            {
                var v = new float[dims];
                for (int i = 0; i < dims; i++) v[i] = (float)r.ReadHalf();
                return v;
            }
            var image = (flags & 1) != 0 ? Read() : null;
            var text = (flags & 2) != 0 ? Read() : null;
            return r.BaseStream.Position == data.Length ? new SemanticVectors(marker, hash, image, text) : null;
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or FormatException or ArgumentException) { return null; }
    }

    /// <summary>Читает sidecar; null — файла нет, он не читается или битый.</summary>
    public static SemanticVectors? TryRead(string? path)
    {
        if (path is null) return null;
        try { return File.Exists(path) ? TryParse(File.ReadAllBytes(path)) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}

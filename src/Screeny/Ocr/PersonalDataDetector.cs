using System.Text;
using System.Text.RegularExpressions;
using System.Windows;

namespace Screeny.Ocr;

/// <summary>Что нашёл <see cref="PersonalDataDetector"/>.</summary>
public enum PersonalDataKind { Email, Phone, Card, Secret, IpAddress }

/// <summary>Найденное в распознанном тексте: вид, текст и рамка слов, которые его содержат (в координатах раскладки).</summary>
public sealed record PersonalDataMatch(PersonalDataKind Kind, string Text, Rect Box);

/// <summary>
/// Поиск личных данных в распознанном тексте для предупреждения в оверлее: адреса почты, телефоны, номера карт
/// (с проверкой Луна), ключи и токены (известные префиксы, JWT, длинные строки с высокой энтропией), IPv4.
/// Чистая логика без UI и OCR.
/// <para>
/// Строка собирается из слов заново (через пробел или слитно, как <see cref="OcrLayout.TextOf"/>), с картой
/// «символ → слово»: номер карты или телефона OCR часто делит на несколько слов. Совпадение отдаёт объединённую рамку
/// всех слов, которые оно задевает. Виды проверяются по порядку (ключи с префиксом, почта, карта, IP, телефон,
/// энтропия), и уже занятые символы второй раз не засчитываются: номер карты не становится ещё и телефоном.
/// </para>
/// Ошибаться лучше в сторону молчания: предупреждение без повода приучает его не читать. Поэтому у телефона без «+»
/// и скобок требования строже, а даты, версии, время, размеры и цены не подходят ни под один вид.
/// </summary>
public static class PersonalDataDetector
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    private static readonly Regex PrefixedSecret = new(
        @"(?<![\w-])(?:sk-[A-Za-z0-9_-]{16,}|sk_(?:live|test)_[A-Za-z0-9]{16,}|gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}" +
        @"|AKIA[0-9A-Z]{16}|xox[bpars]-[A-Za-z0-9-]{10,}|eyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,})",
        RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex Email = new(
        @"(?<![\w.%+-])[A-Za-z0-9][A-Za-z0-9._%+-]*@[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9-]*[A-Za-z0-9])?)*\.[A-Za-z]{2,}(?![\w-])",
        RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>13–19 цифр, между ними одиночные пробелы или дефисы.</summary>
    private static readonly Regex CardCandidate = new(@"(?<!\d[.,-]?)\d(?:[ -]?\d){12,18}(?!\d|[.,-]\d)", RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex Ipv4 = new(
        @"(?<![\w.])(?:(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)\.){3}(?:25[0-5]|2[0-4]\d|1\d\d|[1-9]?\d)(?!\.?\d|\w)",
        RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>Группы цифр (можно в скобках), разделённые пробелом, точкой или дефисом; проверяется в <see cref="IsPhone"/>.</summary>
    private static readonly Regex PhoneCandidate = new(
        @"(?<![\w.+-])(?:\+ ?)?(?:\(\d{1,5}\)|\d{1,5})(?:[ .-]{0,2}(?:\(\d{1,5}\)|\d{1,5})){0,6}(?![\w])",
        RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>Длинная строка из символов base64/hex; энтропию и состав проверяет <see cref="IsHighEntropySecret"/>.</summary>
    private static readonly Regex LongToken = new(@"(?<![\w+/=-])[A-Za-z0-9+/=_-]{32,}(?![\w+/=-])", RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>Минимальная длина строки, которая может оказаться ключом по энтропии.</summary>
    public const int MinSecretLength = 32;

    /// <summary>Всё найденное в раскладке, по строкам в порядке чтения.</summary>
    public static IReadOnlyList<PersonalDataMatch> Detect(OcrLayout layout)
    {
        var result = new List<PersonalDataMatch>();
        foreach (var line in layout.Lines) result.AddRange(Detect(line));
        return result;
    }

    /// <summary>Найденное в одной строке: рамка совпадения — объединение рамок задетых им слов.</summary>
    public static IReadOnlyList<PersonalDataMatch> Detect(OcrLine line)
    {
        if (line.Words.Count == 0) return [];
        var text = new StringBuilder();
        var owner = new List<int>(); // слово для каждого символа строки; разделитель принадлежит предыдущему слову
        for (int w = 0; w < line.Words.Count; w++)
        {
            if (w > 0 && !line.NoSpaces) { text.Append(' '); owner.Add(w - 1); }
            text.Append(line.Words[w].Text);
            for (int i = 0; i < line.Words[w].Text.Length; i++) owner.Add(w);
        }
        var result = new List<PersonalDataMatch>();
        foreach (var (kind, start, length) in Scan(text.ToString()))
        {
            Rect box = Rect.Empty;
            int first = owner[start], last = owner[start + length - 1];
            for (int w = first; w <= last; w++) box.Union(line.Words[w].Box);
            result.Add(new PersonalDataMatch(kind, text.ToString(start, length), box));
        }
        return result;
    }

    /// <summary>Совпадения в строке текста: вид, начало и длина. Совпадения не пересекаются.</summary>
    public static IReadOnlyList<(PersonalDataKind Kind, int Start, int Length)> Scan(string text)
    {
        var found = new List<(PersonalDataKind Kind, int Start, int Length)>();
        if (string.IsNullOrEmpty(text)) return found;
        var taken = new bool[text.Length];

        void Take(Regex regex, PersonalDataKind kind, Func<string, bool>? valid = null)
        {
            try
            {
                foreach (Match m in regex.Matches(text))
                {
                    if (m.Length == 0 || (valid is not null && !valid(m.Value))) continue;
                    bool free = true;
                    for (int i = m.Index; i < m.Index + m.Length && free; i++) free = !taken[i];
                    if (!free) continue;
                    for (int i = m.Index; i < m.Index + m.Length; i++) taken[i] = true;
                    found.Add((kind, m.Index, m.Length));
                }
            }
            catch (RegexMatchTimeoutException) { /* на мусорной строке лучше промолчать, чем зависнуть */ }
        }

        Take(PrefixedSecret, PersonalDataKind.Secret);
        Take(Email, PersonalDataKind.Email);
        Take(CardCandidate, PersonalDataKind.Card, IsCardNumber);
        Take(Ipv4, PersonalDataKind.IpAddress);
        Take(PhoneCandidate, PersonalDataKind.Phone, IsPhone);
        Take(LongToken, PersonalDataKind.Secret, IsHighEntropySecret);
        found.Sort((a, b) => a.Start.CompareTo(b.Start));
        return found;
    }

    /// <summary>
    /// 13–19 цифр, первая от 2 до 6 (Visa, Mastercard, Amex, Discover, JCB, UnionPay, «Мир»; так отсекаются, например,
    /// метки времени в миллисекундах), проходит проверку Луна и это не одна цифра, повторённая много раз.
    /// </summary>
    public static bool IsCardNumber(string candidate)
    {
        var digits = DigitsOf(candidate);
        if (digits.Length is < 13 or > 19 || digits[0] is < '2' or > '6') return false;
        if (digits.All(c => c == digits[0])) return false;
        return Luhn(digits);
    }

    /// <summary>Контрольная сумма Луна для строки из цифр.</summary>
    public static bool Luhn(string digits)
    {
        if (digits.Length == 0) return false;
        int sum = 0;
        bool dbl = false;
        for (int i = digits.Length - 1; i >= 0; i--)
        {
            int d = digits[i] - '0';
            if (d is < 0 or > 9) return false;
            if (dbl) { d *= 2; if (d > 9) d -= 9; }
            sum += d;
            dbl = !dbl;
        }
        return sum % 10 == 0;
    }

    /// <summary>
    /// Телефон: с «+» — 8–15 цифр; со скобками — 10–12; без них — 10–11 цифр в 2–5 группах, где первая группа из
    /// 3+ цифр (или «1»/«8» перед группой из трёх, как 1-800-…, 8 800 …) и есть группа из 3+ цифр. Таблицы чисел
    /// («10 20 30 40 50»), даты, время и версии сюда не проходят.
    /// </summary>
    public static bool IsPhone(string candidate)
    {
        var groups = Regex.Matches(candidate, @"\d+").Select(m => m.Value).ToList();
        int digits = groups.Sum(g => g.Length);
        if (candidate.TrimStart().StartsWith('+')) return digits is >= 8 and <= 15 && (groups.Count > 1 || digits >= 10);
        if (candidate.Contains('(')) return digits is >= 10 and <= 12 && groups.Count >= 2;
        if (digits is < 10 or > 11 || groups.Count is < 2 or > 5) return false;
        bool trunkPrefix = groups[0] is "1" or "8" && groups[1].Length == 3;
        return (groups[0].Length >= 3 || trunkPrefix) && groups.Any(g => g.Length >= 3) && groups.All(g => g.Length <= 4);
    }

    /// <summary>
    /// Длинная строка с высокой энтропией: шестнадцатеричная (хэши, ключи) — от 3 бит на символ, остальные (base64) —
    /// от 4,3 бита, с заглавными и строчными буквами и хотя бы двумя цифрами (случайные 32 символа base64 дают ≈4,5 бита,
    /// длинное имя в camelCase — около 4). Пути и адреса (с «//» или больше двух «/») не считаются.
    /// </summary>
    public static bool IsHighEntropySecret(string token)
    {
        if (token.Length < MinSecretLength) return false;
        if (token.Contains("//") || token.Count(c => c == '/') > 2) return false;
        bool hasDigit = token.Any(char.IsAsciiDigit), hasLetter = token.Any(char.IsAsciiLetter);
        if (!hasDigit || !hasLetter) return false;
        double entropy = Entropy(token);
        if (token.All(char.IsAsciiHexDigit)) return entropy >= 3.0;
        bool upper = token.Any(char.IsAsciiLetterUpper), lower = token.Any(char.IsAsciiLetterLower);
        return upper && lower && token.Count(char.IsAsciiDigit) >= 2 && entropy >= 4.3;
    }

    /// <summary>Энтропия Шеннона строки в битах на символ.</summary>
    public static double Entropy(string s)
    {
        if (s.Length == 0) return 0;
        var counts = new Dictionary<char, int>();
        foreach (var c in s) counts[c] = counts.GetValueOrDefault(c) + 1;
        double h = 0;
        foreach (var n in counts.Values)
        {
            double p = (double)n / s.Length;
            h -= p * Math.Log2(p);
        }
        return h;
    }

    private static string DigitsOf(string s) => new(s.Where(char.IsAsciiDigit).ToArray());
}

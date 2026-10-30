using System.Globalization;
using System.Text;
using Microsoft.ML.Tokenizers;

namespace SemanticProto;

/// <summary>
/// Gemma SentencePiece (BPE, byte fallback, no dummy prefix, split digits) from <c>tokenizer.model</c> via
/// Microsoft.ML.Tokenizers. User text is always plain text: control/user-defined pieces such as <c>&lt;eos&gt;</c> or
/// <c>&lt;|image|&gt;</c> typed in a query must not turn into their special ids (Python reference:
/// <c>split_special_tokens=True</c>).
/// </summary>
public sealed class GemmaTokenizer
{
    public const int Pad = 0, Eos = 1, Bos = 2;
    private readonly SentencePieceTokenizer _sp;

    public GemmaTokenizer(string modelPath)
    {
        using var s = File.OpenRead(modelPath);
        _sp = SentencePieceTokenizer.Create(s, false, false);
    }

    public IReadOnlyList<int> Pieces(string text) =>
        text.Length == 0 ? [] : _sp.EncodeToIds(text, addBeginningOfSentence: false, addEndOfSentence: false);

    /// <summary>SigLIP 2: Python-lowercased text, pieces, EOS; truncated to 63 + EOS; right-padded with 0 to 64.</summary>
    public long[] SiglipIds(string text, int length = 64)
    {
        var p = Pieces(PythonLower(text));
        var ids = new long[length];
        int n = Math.Min(p.Count, length - 1);
        for (int i = 0; i < n; i++) ids[i] = p[i];
        ids[n] = Eos;
        return ids; // rest is Pad (0)
    }

    /// <summary>EmbeddingGemma 2: BOS, pieces, EOS (no padding for a batch of one).</summary>
    public long[] GemmaIds(string text)
    {
        var p = Pieces(text);
        var ids = new long[p.Count + 2];
        ids[0] = Bos;
        for (int i = 0; i < p.Count; i++) ids[i + 1] = p[i];
        ids[^1] = Eos;
        return ids;
    }

    /// <summary>
    /// Python <c>str.lower()</c>: .NET invariant lowercasing plus the two full-case-mapping rules where they differ:
    /// U+0130 (İ) becomes "i̇" (two code points), and capital sigma at the end of a word becomes final sigma (ς).
    /// </summary>
    public static string PythonLower(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == 'İ') { sb.Append("i̇"); continue; }
            if (c == 'Σ') { sb.Append(IsFinalSigma(s, i) ? 'ς' : 'σ'); continue; }
            if (char.IsHighSurrogate(c) && i + 1 < s.Length)
            {
                sb.Append(char.ConvertFromUtf32(LowerRune(char.ConvertToUtf32(c, s[i + 1]))));
                i++;
                continue;
            }
            sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    private static int LowerRune(int cp) => Rune.ToLowerInvariant(new Rune(cp)).Value;

    // Unicode Final_Sigma: preceded by a cased letter (skipping case-ignorable), not followed by one.
    private static bool IsFinalSigma(string s, int i)
    {
        int j = i - 1;
        while (j >= 0 && IsCaseIgnorable(s[j])) j--;
        if (j < 0 || !char.IsLetter(s[j])) return false;
        int k = i + 1;
        while (k < s.Length && IsCaseIgnorable(s[k])) k++;
        return k >= s.Length || !char.IsLetter(s[k]);
    }

    private static bool IsCaseIgnorable(char c) => CharUnicodeInfo.GetUnicodeCategory(c) is
        UnicodeCategory.NonSpacingMark or UnicodeCategory.EnclosingMark or UnicodeCategory.Format or
        UnicodeCategory.ModifierLetter or UnicodeCategory.ModifierSymbol || c is '\'' or '.' or ':' or '·' or '’';
}

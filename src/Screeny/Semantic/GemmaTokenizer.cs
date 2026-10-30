using Microsoft.ML.Tokenizers;

namespace Screeny.Semantic;

/// <summary>
/// Токенизатор Gemma (SentencePiece BPE, byte fallback, без dummy prefix, цифры по одной) из <c>tokenizer.model</c> модуля.
/// Текст пользователя — всегда просто текст: набранные в запросе <c>&lt;eos&gt;</c> или <c>&lt;|image|&gt;</c> не превращаются
/// в служебные id (как <c>split_special_tokens=True</c> в transformers; на 60 строках разных языков id совпадают с Python,
/// см. tools/semantic-module/README.md).
/// </summary>
public sealed class GemmaTokenizer
{
    public const int Eos = 1, Bos = 2;
    private readonly SentencePieceTokenizer _sp;

    public GemmaTokenizer(string modelPath)
    {
        using var s = File.OpenRead(modelPath);
        _sp = SentencePieceTokenizer.Create(s, false, false);
    }

    /// <summary>BOS, куски текста, EOS; длиннее <paramref name="maxTokens"/> — обрезается (EOS остаётся последним).</summary>
    public long[] Ids(string text, int maxTokens = int.MaxValue)
    {
        var pieces = text.Length == 0 ? [] : _sp.EncodeToIds(text, addBeginningOfSentence: false, addEndOfSentence: false);
        int n = Math.Min(pieces.Count, Math.Max(0, maxTokens - 2));
        var ids = new long[n + 2];
        ids[0] = Bos;
        for (int i = 0; i < n; i++) ids[i + 1] = pieces[i];
        ids[^1] = Eos;
        return ids;
    }
}

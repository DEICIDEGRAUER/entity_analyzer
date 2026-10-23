using Microsoft.ML.Tokenizers;

namespace EntityAnalyzer.Embedders.Onnx.Internal;

/// <summary>
/// XLM-R SentencePiece tokenizer with the fairseq ID remapping used by
/// Hugging Face's XLMRobertaTokenizer / E5 profiles.
///
/// The raw sentencepiece.bpe.model uses:
///   0 = &lt;unk&gt;, 1 = &lt;s&gt;, 2 = &lt;/s&gt;, 3 = ',', 4 = '.', ...
/// E5's ONNX embedding matrix expects the tokenizer.json layout:
///   0 = &lt;s&gt;, 1 = &lt;pad&gt;, 2 = &lt;/s&gt;, 3 = &lt;unk&gt;,
///   4 = ',', 5 = '.', ... (all regular tokens are shifted by +1).
///
/// SentencePieceTokenizer does not apply the fairseq offset itself, so this
/// wrapper is essential for correct E5 embeddings. It uses only the
/// SentencePiece model at runtime; tokenizer.json is not parsed by production code.
/// </summary>
internal sealed class E5Tokenizer : IE5Tokenizer
{
    public const long XlmRobertaBeginningOfSentenceId = 0;
    public const long XlmRobertaPaddingId = 1;
    public const long XlmRobertaEndOfSentenceId = 2;
    public const long XlmRobertaUnknownId = 3;

    private const long RawBeginningOfSentenceId = 1;
    private const long RawEndOfSentenceId = 2;
    private const long RawUnknownId = 0;

    private readonly SentencePieceTokenizer _tokenizer;

    private E5Tokenizer(SentencePieceTokenizer tokenizer)
    {
        _tokenizer = tokenizer;
    }

    public long BeginningOfSentenceId => XlmRobertaBeginningOfSentenceId;

    public long EndOfSentenceId => XlmRobertaEndOfSentenceId;

    public static E5Tokenizer Load(string path)
    {
        using var stream = File.OpenRead(path);
        var tokenizer = SentencePieceTokenizer.Create(
            stream,
            addBeginningOfSentence: true,
            addEndOfSentence: true,
            specialTokens: new Dictionary<string, int>());

        if (tokenizer.BeginningOfSentenceId != RawBeginningOfSentenceId ||
            tokenizer.EndOfSentenceId != RawEndOfSentenceId ||
            tokenizer.UnknownId != RawUnknownId)
        {
            throw new InvalidOperationException(
                "Unexpected XLM-R SentencePiece special-token layout. " +
                $"Expected raw BOS={RawBeginningOfSentenceId}, EOS={RawEndOfSentenceId}, " +
                $"UNK={RawUnknownId}; got BOS={tokenizer.BeginningOfSentenceId}, " +
                $"EOS={tokenizer.EndOfSentenceId}, UNK={tokenizer.UnknownId}.");
        }

        return new E5Tokenizer(tokenizer);
    }

    public IReadOnlyList<long> Encode(string text)
    {
        var rawIds = _tokenizer.EncodeToIds(
            text,
            addBeginningOfSentence: true,
            addEndOfSentence: true,
            considerPreTokenization: true,
            considerNormalization: true);

        var ids = new long[rawIds.Count];
        for (var index = 0; index < rawIds.Count; index++)
            ids[index] = MapRawId(rawIds[index]);
        return ids;
    }

    internal static long MapRawId(long rawId) => rawId switch
    {
        0 => XlmRobertaUnknownId,
        1 => XlmRobertaBeginningOfSentenceId,
        2 => XlmRobertaEndOfSentenceId,
        _ when rawId >= 0 => rawId + 1,
        _ => throw new ArgumentOutOfRangeException(nameof(rawId), rawId, "SentencePiece token id cannot be negative."),
    };
}

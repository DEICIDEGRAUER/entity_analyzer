namespace EntityAnalyzer.Embedders.Onnx.Internal;

internal interface IE5Tokenizer
{
    long BeginningOfSentenceId { get; }

    long EndOfSentenceId { get; }

    IReadOnlyList<long> Encode(string text);
}

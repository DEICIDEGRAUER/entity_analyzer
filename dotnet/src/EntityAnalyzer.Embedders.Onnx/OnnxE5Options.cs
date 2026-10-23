namespace EntityAnalyzer.Embedders.Onnx;

/// <summary>
/// Explicit configuration for the optional E5 ONNX embedder.
/// Model files are never discovered or downloaded implicitly at runtime.
/// </summary>
public sealed record OnnxE5Options
{
    /// <summary>Directory containing the pinned E5 profile files.</summary>
    public required string ModelDirectory { get; init; }

    /// <summary>Model path relative to <see cref="ModelDirectory"/>.</summary>
    public string ModelFile { get; init; } = Path.Combine("onnx", "model_int8.onnx");

    /// <summary>XLM-R SentencePiece model path relative to <see cref="ModelDirectory"/>.</summary>
    public string TokenizerFile { get; init; } = "sentencepiece.bpe.model";

    /// <summary>Maximum sequence length including BOS and EOS. E5 supports 512.</summary>
    public int MaxTokens { get; init; } = OnnxE5Embedder.MaximumTokens;
}

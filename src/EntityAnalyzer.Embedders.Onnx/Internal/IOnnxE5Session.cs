namespace EntityAnalyzer.Embedders.Onnx.Internal;

internal interface IOnnxE5Session : IDisposable
{
    int HiddenSize { get; }

    float[] Infer(IReadOnlyList<long> inputIds, IReadOnlyList<long> attentionMask);
}

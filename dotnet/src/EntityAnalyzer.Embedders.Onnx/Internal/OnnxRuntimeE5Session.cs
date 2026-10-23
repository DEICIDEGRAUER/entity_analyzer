using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace EntityAnalyzer.Embedders.Onnx.Internal;

/// <summary>
/// Thin ONNX Runtime adapter for the Xenova multilingual-e5-small int8 model.
/// The model has three int64 inputs (input_ids, attention_mask, token_type_ids)
/// and one float output (last_hidden_state) of shape [1, sequence, 384].
/// </summary>
internal sealed class OnnxRuntimeE5Session : IOnnxE5Session
{
    private const string InputIdsName = "input_ids";
    private const string AttentionMaskName = "attention_mask";
    private const string TokenTypeIdsName = "token_type_ids";
    private const string LastHiddenStateName = "last_hidden_state";

    private readonly InferenceSession _session;
    private readonly string _inputIdsName;
    private readonly string _attentionMaskName;
    private readonly string? _tokenTypeIdsName;
    private readonly string _outputName;

    public OnnxRuntimeE5Session(string modelPath)
    {
        using var options = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        };

        _session = new InferenceSession(modelPath, options);

        try
        {
            _inputIdsName = RequireInt64Input(_session, InputIdsName);
            _attentionMaskName = RequireInt64Input(_session, AttentionMaskName);
            _tokenTypeIdsName = FindInput(_session, TokenTypeIdsName) is { } tokenTypeIds
                ? RequireInt64Input(_session, tokenTypeIds)
                : null;
            _outputName = FindOutput(_session, LastHiddenStateName) ?? _session.OutputMetadata.Keys.First();

            var output = _session.OutputMetadata[_outputName];
            if (output.ElementType != typeof(float))
            {
                throw new InvalidOperationException(
                    $"E5 output '{_outputName}' must be float32, got {output.ElementType}.");
            }

            if (output.Dimensions.Length == 3 &&
                output.Dimensions[2] > 0 &&
                output.Dimensions[2] != OnnxE5Embedder.EmbeddingDimensions)
            {
                throw new InvalidOperationException(
                    $"E5 output hidden size must be {OnnxE5Embedder.EmbeddingDimensions}, " +
                    $"got {output.Dimensions[2]}.");
            }
        }
        catch
        {
            _session.Dispose();
            throw;
        }
    }

    public int HiddenSize => OnnxE5Embedder.EmbeddingDimensions;

    public float[] Infer(IReadOnlyList<long> inputIds, IReadOnlyList<long> attentionMask)
    {
        if (inputIds.Count == 0)
            throw new ArgumentException("Input ids cannot be empty.", nameof(inputIds));
        if (inputIds.Count != attentionMask.Count)
        {
            throw new ArgumentException(
                "Input ids and attention mask must have the same length.",
                nameof(attentionMask));
        }

        var sequenceLength = inputIds.Count;
        var idsTensor = CreateLongTensor(inputIds);
        var maskTensor = CreateLongTensor(attentionMask);
        var inputs = new List<NamedOnnxValue>(3)
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName, idsTensor),
            NamedOnnxValue.CreateFromTensor(_attentionMaskName, maskTensor),
        };

        if (_tokenTypeIdsName is not null)
        {
            var tokenTypeIds = new long[sequenceLength];
            inputs.Add(NamedOnnxValue.CreateFromTensor(
                _tokenTypeIdsName,
                CreateLongTensor(tokenTypeIds)));
        }

        using var results = _session.Run(inputs);
        var output = results.First(result => result.Name == _outputName);
        var tensor = output.AsTensor<float>();
        var dimensions = tensor.Dimensions.ToArray();

        if (dimensions.Length != 3 ||
            dimensions[0] != 1 ||
            dimensions[1] != sequenceLength ||
            dimensions[2] != HiddenSize)
        {
            throw new InvalidOperationException(
                $"Unexpected E5 output shape [{string.Join(", ", dimensions)}]; " +
                $"expected [1, {sequenceLength}, {HiddenSize}].");
        }

        return tensor.ToArray();
    }

    private static DenseTensor<long> CreateLongTensor(IReadOnlyList<long> values)
    {
        var tensor = new DenseTensor<long>(new[] { 1, values.Count });
        var buffer = tensor.Buffer.Span;
        for (var index = 0; index < values.Count; index++)
            buffer[index] = values[index];
        return tensor;
    }

    private static string RequireInt64Input(InferenceSession session, string name)
    {
        var actualName = FindInput(session, name)
            ?? throw new InvalidOperationException(
                $"E5 ONNX model is missing required input '{name}'.");

        var metadata = session.InputMetadata[actualName];
        if (metadata.ElementType != typeof(long))
        {
            throw new InvalidOperationException(
                $"E5 input '{actualName}' must be int64, got {metadata.ElementType}.");
        }

        return actualName;
    }

    private static string? FindInput(InferenceSession session, string name)
    {
        if (session.InputMetadata.ContainsKey(name))
            return name;

        return session.InputMetadata.Keys.FirstOrDefault(
            key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
    }

    private static string? FindOutput(InferenceSession session, string name)
    {
        if (session.OutputMetadata.ContainsKey(name))
            return name;

        return session.OutputMetadata.Keys.FirstOrDefault(
            key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase));
    }

    public void Dispose() => _session.Dispose();
}

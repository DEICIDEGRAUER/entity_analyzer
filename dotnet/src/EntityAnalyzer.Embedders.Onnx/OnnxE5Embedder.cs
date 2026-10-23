using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Embedders.Onnx.Internal;

namespace EntityAnalyzer.Embedders.Onnx;

/// <summary>
/// Optional 384-dimensional multilingual E5 embedder backed by ONNX Runtime.
/// BoW remains the default; callers must construct this class explicitly.
/// </summary>
public sealed class OnnxE5Embedder : IRoleAwareEmbedder, IDisposable
{
    public const int EmbeddingDimensions = 384;
    public const int MaximumTokens = 512;

    private const string DocumentPrefix = "passage: ";
    private const string QueryPrefix = "query: ";

    private readonly IOnnxE5Session _session;
    private readonly IE5Tokenizer _tokenizer;
    private readonly int _maxTokens;
    private readonly object _gate = new();
    private bool _disposed;

    public OnnxE5Embedder(OnnxE5Options options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _maxTokens = ValidateMaxTokens(options.MaxTokens);
        var modelPath = ResolveRequiredFile(
            options.ModelDirectory,
            options.ModelFile,
            "E5 ONNX model");
        var tokenizerPath = ResolveRequiredFile(
            options.ModelDirectory,
            options.TokenizerFile,
            "E5 SentencePiece tokenizer");

        var session = new OnnxRuntimeE5Session(modelPath);
        try
        {
            _session = session;
            _tokenizer = E5Tokenizer.Load(tokenizerPath);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    internal OnnxE5Embedder(IOnnxE5Session session, IE5Tokenizer tokenizer, int maxTokens = MaximumTokens)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(tokenizer);

        if (session.HiddenSize != EmbeddingDimensions)
        {
            throw new ArgumentException(
                $"E5 session hidden size must be {EmbeddingDimensions}, got {session.HiddenSize}.",
                nameof(session));
        }

        _session = session;
        _tokenizer = tokenizer;
        _maxTokens = ValidateMaxTokens(maxTokens);
    }

    public int Dimensions => EmbeddingDimensions;

    public double[] Embed(string text) => Embed(text, EmbeddingRole.Document);

    public double[] Embed(string text, EmbeddingRole role)
    {
        ArgumentNullException.ThrowIfNull(text);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var prefix = role switch
        {
            EmbeddingRole.Document => DocumentPrefix,
            EmbeddingRole.Query => QueryPrefix,
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Unknown embedding role."),
        };

        if (string.IsNullOrWhiteSpace(text))
            return new double[EmbeddingDimensions];

        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var ids = PrepareIds(_tokenizer.Encode(prefix + text));
            var attentionMask = new long[ids.Length];
            Array.Fill(attentionMask, 1L);
            var hiddenState = _session.Infer(ids, attentionMask);
            return PoolAndNormalize(hiddenState, attentionMask, EmbeddingDimensions);
        }
    }

    /// <summary>
    /// Attention-mask-aware mean pooling followed by L2 normalization.
    /// Kept internal so the numerical contract can be tested without model weights.
    /// </summary>
    internal static double[] PoolAndNormalize(
        ReadOnlySpan<float> hiddenState,
        ReadOnlySpan<long> attentionMask,
        int hiddenSize)
    {
        if (hiddenSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(hiddenSize));
        if (attentionMask.Length == 0)
            return new double[hiddenSize];
        if (hiddenState.Length != attentionMask.Length * hiddenSize)
        {
            throw new ArgumentException(
                "Hidden state length does not match attention-mask length times hidden size.",
                nameof(hiddenState));
        }

        var vector = new double[hiddenSize];
        var tokenCount = 0;

        for (var token = 0; token < attentionMask.Length; token++)
        {
            if (attentionMask[token] == 0)
                continue;

            tokenCount++;
            var offset = token * hiddenSize;
            for (var dimension = 0; dimension < hiddenSize; dimension++)
                vector[dimension] += hiddenState[offset + dimension];
        }

        if (tokenCount == 0)
            return new double[hiddenSize];

        var normSquared = 0.0;
        for (var dimension = 0; dimension < hiddenSize; dimension++)
        {
            vector[dimension] /= tokenCount;
            normSquared += vector[dimension] * vector[dimension];
        }

        if (normSquared < 1e-24)
            return new double[hiddenSize];

        var norm = Math.Sqrt(normSquared);
        for (var dimension = 0; dimension < hiddenSize; dimension++)
            vector[dimension] /= norm;

        return vector;
    }

    internal long[] PrepareIds(IReadOnlyList<long> encoded)
    {
        if (encoded.Count == 0)
        {
            throw new InvalidOperationException(
                "E5 tokenizer returned no tokens for a non-empty input. " +
                "Check that sentencepiece.bpe.model matches the pinned profile.");
        }

        if (encoded[0] != _tokenizer.BeginningOfSentenceId ||
            encoded[^1] != _tokenizer.EndOfSentenceId)
        {
            throw new InvalidOperationException(
                "E5 tokenizer must emit BOS and EOS tokens explicitly; " +
                "the pinned XLM-R profile uses <s> ... </s>.");
        }

        if (encoded.Count <= _maxTokens)
            return [.. encoded];

        // Reserve the last position for EOS, matching HF truncation with
        // add_special_tokens=true and max_length=maxTokens.
        var truncated = new long[_maxTokens];
        for (var index = 0; index < _maxTokens - 1; index++)
            truncated[index] = encoded[index];
        truncated[^1] = _tokenizer.EndOfSentenceId;
        return truncated;
    }

    private static int ValidateMaxTokens(int maxTokens)
    {
        if (maxTokens is < 2 or > MaximumTokens)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxTokens),
                maxTokens,
                $"E5 max token count must be between 2 and {MaximumTokens}.");
        }

        return maxTokens;
    }

    private static string ResolveRequiredFile(
        string modelDirectory,
        string relativePath,
        string description)
    {
        if (string.IsNullOrWhiteSpace(modelDirectory))
        {
            throw new ArgumentException(
                "ModelDirectory is required. It must point to the explicitly fetched E5 profile.",
                nameof(modelDirectory));
        }

        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ArgumentException($"{description} path is required.", nameof(relativePath));
        if (Path.IsPathRooted(relativePath))
            throw new ArgumentException($"{description} path must be relative to ModelDirectory.", nameof(relativePath));

        var root = Path.GetFullPath(modelDirectory);
        var path = Path.GetFullPath(Path.Combine(root, relativePath));
        var relative = Path.GetRelativePath(root, path);
        if (Path.IsPathRooted(relative) ||
            relative == ".." ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"{description} path escapes ModelDirectory: {relativePath}",
                nameof(relativePath));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"{description} not found: '{path}'. " +
                "Fetch it explicitly with `scripts/fetch-model.ps1 -Model e5-small` on Windows " +
                "or `scripts/fetch-model.sh -Model e5-small` on Linux/macOS. " +
                "The application never downloads models automatically.",
                path);
        }

        return path;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _session.Dispose();
            _disposed = true;
        }
    }
}

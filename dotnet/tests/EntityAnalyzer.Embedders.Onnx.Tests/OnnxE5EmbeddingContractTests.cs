using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Embedders.Onnx.Internal;
using Xunit;

namespace EntityAnalyzer.Embedders.Onnx.Tests;

public class OnnxE5EmbeddingContractTests
{
    private const int Dimensions = OnnxE5Embedder.EmbeddingDimensions;

    private sealed class FakeSession : IOnnxE5Session
    {
        public int HiddenSize => Dimensions;
        public int Calls { get; private set; }
        public IReadOnlyList<long> LastInputIds { get; private set; } = [];

        public float[] Infer(IReadOnlyList<long> inputIds, IReadOnlyList<long> attentionMask)
        {
            Calls++;
            LastInputIds = inputIds.ToArray();

            var data = new float[inputIds.Count * Dimensions];
            for (var token = 0; token < inputIds.Count; token++)
            {
                for (var dimension = 0; dimension < Dimensions; dimension++)
                {
                    data[token * Dimensions + dimension] =
                        (inputIds[token] + 1) * (dimension + 1) / 1000f;
                }
            }

            return data;
        }

        public void Dispose()
        {
        }
    }

    private sealed class FakeTokenizer : IE5Tokenizer
    {
        public long BeginningOfSentenceId => E5Tokenizer.XlmRobertaBeginningOfSentenceId;
        public long EndOfSentenceId => E5Tokenizer.XlmRobertaEndOfSentenceId;
        public List<string> Inputs { get; } = [];

        public IReadOnlyList<long> Encode(string text)
        {
            Inputs.Add(text);
            return text.StartsWith("query: ", StringComparison.Ordinal)
                ? [0, 7, 8, 2]
                : [0, 9, 10, 2];
        }
    }

    [Fact]
    public void Contract_is_384_dimensional_and_empty_input_is_zero_without_model_calls()
    {
        var session = new FakeSession();
        var tokenizer = new FakeTokenizer();
        using var embedder = new OnnxE5Embedder(session, tokenizer);

        Assert.Equal(384, embedder.Dimensions);

        var empty = embedder.Embed("", EmbeddingRole.Query);
        Assert.Equal(384, empty.Length);
        Assert.All(empty, value => Assert.Equal(0.0, value));

        var whitespace = embedder.Embed(" \t\r\n ", EmbeddingRole.Document);
        Assert.Equal(384, whitespace.Length);
        Assert.All(whitespace, value => Assert.Equal(0.0, value));

        Assert.Empty(tokenizer.Inputs);
        Assert.Equal(0, session.Calls);
    }

    [Fact]
    public void Masked_mean_pooling_ignores_padding_and_normalizes()
    {
        var hidden = new float[] { 1f, 0f, 99f, 99f, 3f, 4f };
        var mask = new long[] { 1, 0, 1 };

        var pooled = OnnxE5Embedder.PoolAndNormalize(hidden, mask, hiddenSize: 2);

        Assert.Equal(Math.Sqrt(0.5), pooled[0], 10);
        Assert.Equal(Math.Sqrt(0.5), pooled[1], 10);
        Assert.Equal(1.0, Norm(pooled), 10);
        Assert.All(
            OnnxE5Embedder.PoolAndNormalize(hidden, [0L, 0L, 0L], 2),
            value => Assert.Equal(0.0, value));
    }

    [Fact]
    public void Embeddings_are_l2_normalized_and_role_prefixes_reach_tokenizer()
    {
        var session = new FakeSession();
        var tokenizer = new FakeTokenizer();
        using var embedder = new OnnxE5Embedder(session, tokenizer);

        var document = embedder.Embed("hello", EmbeddingRole.Document);
        var query = embedder.Embed("hello", EmbeddingRole.Query);

        Assert.Equal(384, document.Length);
        Assert.Equal(384, query.Length);
        Assert.Equal(1.0, Norm(document), 10);
        Assert.Equal(1.0, Norm(query), 10);
        Assert.False(document.SequenceEqual(query));
        Assert.Equal(["passage: hello", "query: hello"], tokenizer.Inputs);
    }

    [Fact]
    public void Truncation_reserves_bos_and_eos()
    {
        var session = new FakeSession();
        var tokenizer = new FakeTokenizer();
        using var embedder = new OnnxE5Embedder(session, tokenizer, maxTokens: 4);

        var encoded = Enumerable.Range(0, 20).Select(value => (long)value).ToArray();
        encoded[0] = E5Tokenizer.XlmRobertaBeginningOfSentenceId;
        encoded[^1] = E5Tokenizer.XlmRobertaEndOfSentenceId;

        var prepared = embedder.PrepareIds(encoded);

        Assert.Equal(4, prepared.Length);
        Assert.Equal(E5Tokenizer.XlmRobertaBeginningOfSentenceId, prepared[0]);
        Assert.Equal(E5Tokenizer.XlmRobertaEndOfSentenceId, prepared[^1]);

        embedder.Embed("hello", EmbeddingRole.Document);
        Assert.Equal(4, session.LastInputIds.Count);
        Assert.Equal(E5Tokenizer.XlmRobertaBeginningOfSentenceId, session.LastInputIds[0]);
        Assert.Equal(E5Tokenizer.XlmRobertaEndOfSentenceId, session.LastInputIds[^1]);
    }

    [Fact]
    public void VectorIndex_uses_document_and_query_roles_with_onnx_embedder()
    {
        using var embedder = new OnnxE5Embedder(new FakeSession(), new FakeTokenizer());
        var index = new VectorIndex(embedder);

        index.Add("doc", "hello");
        var result = Assert.Single(index.Search("hello", 1));

        Assert.Equal("doc", result.Id);
    }

    [Fact]
    public void Missing_model_reports_explicit_fetch_command()
    {
        var missingDirectory = Path.Combine(
            Path.GetTempPath(),
            $"ea-missing-e5-{Guid.NewGuid():N}");

        var exception = Assert.Throws<FileNotFoundException>(
            () => new OnnxE5Embedder(new OnnxE5Options { ModelDirectory = missingDirectory }));

        Assert.Contains("fetch-model.ps1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("e5-small", exception.Message, StringComparison.Ordinal);
    }

    private static double Norm(IReadOnlyList<double> vector)
        => Math.Sqrt(vector.Sum(value => value * value));
}

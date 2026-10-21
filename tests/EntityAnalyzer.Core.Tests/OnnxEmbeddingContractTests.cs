using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// Phase B: контракт будущего ONNX-эмбеддера E5 фиксируется заглушкой,
/// без Microsoft.ML.OnnxRuntime и без модели в репозитории.
/// </summary>
public class OnnxEmbeddingContractTests
{
    private const int E5Dimensions = 384;

    private sealed class StubE5Embedder : IRoleAwareEmbedder
    {
        public int Dimensions => E5Dimensions;

        public double[] Embed(string text) => Embed(text, EmbeddingRole.Document);

        public double[] Embed(string text, EmbeddingRole role)
        {
            if (text is null) throw new ArgumentNullException(nameof(text));

            var vector = new double[Dimensions];
            if (string.IsNullOrWhiteSpace(text)) return vector;

            var prefix = role == EmbeddingRole.Query ? "query: " : "passage: ";
            foreach (var raw in (prefix + text).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = raw.Trim(',', '.', ';', ':', '!', '?').ToLowerInvariant();
                if (token.Length == 0) continue;
                vector[StableBucket(token)] += 1;
            }

            var norm = Math.Sqrt(vector.Sum(value => value * value));
            if (norm > 0)
                for (var i = 0; i < vector.Length; i++) vector[i] /= norm;
            return vector;
        }

        private int StableBucket(string token)
        {
            var hash = 2166136261u;
            unchecked
            {
                foreach (var c in token)
                {
                    hash ^= c;
                    hash *= 16777619u;
                }
            }

            return (int)(hash % (uint)Dimensions);
        }
    }

    private sealed class RecordingEmbedder : IRoleAwareEmbedder
    {
        public int Dimensions => 2;
        public List<EmbeddingRole> Roles { get; } = [];

        public double[] Embed(string text) => Embed(text, EmbeddingRole.Document);

        public double[] Embed(string text, EmbeddingRole role)
        {
            Roles.Add(role);
            return role == EmbeddingRole.Query ? [1.0, 0.0] : [0.0, 1.0];
        }
    }

    [Fact]
    public void Onnx_profile_stub_is_384_dimensional_and_empty_input_is_zero()
    {
        var embedder = new StubE5Embedder();
        Assert.Equal(384, embedder.Dimensions);

        var empty = embedder.Embed("", EmbeddingRole.Query);
        Assert.Equal(384, empty.Length);
        Assert.All(empty, value => Assert.Equal(0.0, value));

        var whitespace = embedder.Embed(" \t\r\n ", EmbeddingRole.Document);
        Assert.Equal(384, whitespace.Length);
        Assert.All(whitespace, value => Assert.Equal(0.0, value));
    }

    [Fact]
    public void Onnx_profile_stub_is_l2_normalized_and_role_sensitive()
    {
        var embedder = new StubE5Embedder();
        var query = embedder.Embed("море шторм", EmbeddingRole.Query);
        var document = embedder.Embed("море шторм", EmbeddingRole.Document);

        Assert.Equal(1.0, Math.Sqrt(query.Sum(value => value * value)), 12);
        Assert.Equal(1.0, Math.Sqrt(document.Sum(value => value * value)), 12);
        Assert.False(query.SequenceEqual(document));

        var index = new VectorIndex(embedder);
        index.Add("doc", "море шторм");
        Assert.Single(index.Search("шторм", 1));
    }

    [Fact]
    public void Role_aware_index_uses_document_for_add_and_query_for_search()
    {
        var embedder = new RecordingEmbedder();
        var index = new VectorIndex(embedder);

        index.Add("doc", "text");
        Assert.Equal(new[] { EmbeddingRole.Document }, embedder.Roles);

        Assert.Single(index.Search("text", 1));
        Assert.Equal(new[] { EmbeddingRole.Document, EmbeddingRole.Query }, embedder.Roles);

        Assert.Empty(index.Search(" ", 1));
        Assert.Equal(2, embedder.Roles.Count);
    }
}

using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Embedders.Onnx.Tests;

/// <summary>
/// End-to-end retrieval usefulness checks: a real VectorIndex, the real int8
/// embedder, and queries whose correct document is not the lexically closest
/// one. This is topical retrieval, not homonym disambiguation. Numeric
/// correctness is covered separately by OnnxGoldenTests; HomonymClassifier is
/// the project's disambiguation mechanism.
/// </summary>
[Trait("Category", "OnnxRetrieval")]
public class OnnxRetrievalTests
{
    private const string TolstoyDocument =
        "Лев Толстой написал роман «Война и мир».";

    private const string PushkinDocument =
        "Александр Пушкин написал роман «Евгений Онегин».";

    [ModelFact]
    public void VectorIndex_ranks_the_titled_document_first()
    {
        using var embedder = CreateEmbedder();
        var index = CreateIndex(embedder);

        var results = index.Search("кто написал роман «Война и мир»", topK: 2);

        Assert.Equal(2, results.Count);
        Assert.Equal("tolstoy", results[0].Id);
        Assert.True(
            results[0].Score > results[1].Score,
            $"Expected Tolstoy above Pushkin, got {results[0].Score:R} <= {results[1].Score:R}.");
    }

    [ModelFact]
    public void VectorIndex_ranks_a_paraphrase_above_the_distractor()
    {
        using var embedder = CreateEmbedder();
        var index = CreateIndex(embedder);

        // No title, author, or canonical phrase from the target document.
        var results = index.Search("произведение о войне 1812 года", topK: 2);

        Assert.Equal(2, results.Count);
        Assert.Equal("tolstoy", results[0].Id);
        Assert.True(
            results[0].Score > results[1].Score,
            $"Expected the Tolstoy paraphrase above Pushkin, got " +
            $"{results[0].Score:R} <= {results[1].Score:R}.");
    }

    private static OnnxE5Embedder CreateEmbedder() =>
        new(new OnnxE5Options
        {
            ModelDirectory = OnnxTestPaths.ModelDirectory!,
        });

    private static VectorIndex CreateIndex(OnnxE5Embedder embedder)
    {
        var index = new VectorIndex(embedder);
        index.Add("tolstoy", TolstoyDocument);
        index.Add("pushkin", PushkinDocument);
        return index;
    }
}

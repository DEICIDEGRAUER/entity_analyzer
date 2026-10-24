using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Embedders.Onnx.Tests;

/// <summary>
/// End-to-end usefulness checks: a real VectorIndex, the real int8 embedder,
/// and a query whose correct document is not the lexically closest one.
/// Numeric correctness is covered separately by OnnxGoldenTests.
/// </summary>
[Trait("Category", "OnnxSemantic")]
public class OnnxSemanticTests
{
    [ModelFact]
    public void VectorIndex_ranks_the_semantically_matching_document_first()
    {
        using var embedder = new OnnxE5Embedder(new OnnxE5Options
        {
            ModelDirectory = OnnxTestPaths.ModelDirectory!,
        });

        var index = new VectorIndex(embedder);
        index.Add("tolstoy", "Лев Толстой написал роман «Война и мир».");
        index.Add("pushkin", "Александр Пушкин написал роман «Евгений Онегин».");

        var results = index.Search("кто написал роман «Война и мир»", topK: 2);

        Assert.Equal(2, results.Count);
        Assert.Equal("tolstoy", results[0].Id);
        Assert.True(
            results[0].Score > results[1].Score,
            $"Expected Tolstoy above Pushkin, got {results[0].Score:R} <= {results[1].Score:R}.");
    }
}

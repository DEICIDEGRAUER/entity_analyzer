using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Embedders.Onnx.Tests;

public class OnnxE5ModelTests
{
    [ModelFact]
    public void Model_loads_and_produces_normalized_deterministic_384_dimensional_vectors()
    {
        using var embedder = new OnnxE5Embedder(new OnnxE5Options
        {
            ModelDirectory = OnnxTestPaths.ModelDirectory!,
        });

        var document = embedder.Embed("Привет, world!", EmbeddingRole.Document);
        var query = embedder.Embed("Привет, world!", EmbeddingRole.Query);
        var documentAgain = embedder.Embed("Привет, world!", EmbeddingRole.Document);

        Assert.Equal(384, document.Length);
        Assert.Equal(384, query.Length);
        Assert.Equal(1.0, Norm(document), 6);
        Assert.Equal(1.0, Norm(query), 6);
        Assert.False(document.SequenceEqual(query));
        Assert.Equal(document, documentAgain);
    }

    private static double Norm(IReadOnlyList<double> vector)
        => Math.Sqrt(vector.Sum(value => value * value));
}

using System.Text.Json;
using System.Text.Json.Serialization;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Embedders.Onnx.Internal;
using Xunit;

namespace EntityAnalyzer.Embedders.Onnx.Tests;

/// <summary>
/// Numerical anchors generated from the same int8 ONNX profile used by the
/// C# embedder. These tests are deliberately separated from the ordinary
/// model-gated contract tests by the OnnxGolden trait.
/// </summary>
[Trait("Category", "OnnxGolden")]
public class OnnxGoldenTests
{
    // The Windows int8 kernel matches the generator to >0.9999. GitHub's
    // heterogeneous Ubuntu runners can select a different quantized MatMul
    // kernel; one run measured 0.996705 for ru-query while the next runner was
    // above 0.9999. Token IDs remain exact everywhere. Keep the strict Windows
    // anchor and use a floor that still catches prefix/pooling/truncation bugs
    // on other runtimes.
    private static double Int8CosineMin =>
        OperatingSystem.IsWindows() ? 0.9999 : 0.99;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    [GoldenFact]
    public void Golden_int8_token_ids_match_csharp_tokenizer_and_truncation()
    {
        var golden = LoadGolden();
        var tokenizer = E5Tokenizer.Load(OnnxTestPaths.TokenizerPath!);
        using var embedder = CreateEmbedder();

        foreach (var testCase in golden.Cases)
        {
            var encoded = tokenizer.Encode(Prefix(testCase.Role) + testCase.Text);
            var prepared = embedder.PrepareIds(encoded);

            Assert.Equal(testCase.InputIds, prepared);
            Assert.Equal(testCase.UsedTokens, prepared.Length);
            Assert.Equal(testCase.Truncated, encoded.Count > OnnxE5Embedder.MaximumTokens);

            if (testCase.Truncated)
                Assert.Equal(OnnxE5Embedder.MaximumTokens, prepared.Length);
        }
    }

    [GoldenFact]
    public void Golden_int8_vectors_match_csharp_embedder()
    {
        var golden = LoadGolden();
        using var embedder = CreateEmbedder();
        var similarities = new List<(string Id, double Value)>();

        foreach (var testCase in golden.Cases)
        {
            var actual = embedder.Embed(testCase.Text, ParseRole(testCase.Role));

            Assert.Equal(OnnxE5Embedder.EmbeddingDimensions, actual.Length);
            Assert.InRange(Norm(actual), 1.0 - 1e-6, 1.0 + 1e-6);

            similarities.Add((testCase.Id, Cosine(actual, testCase.Vector)));
        }

        var minimum = similarities.Min(entry => entry.Value);
        var mean = similarities.Average(entry => entry.Value);
        var failures = similarities
            .Where(entry => entry.Value <= Int8CosineMin)
            .Select(entry => $"{entry.Id}={entry.Value:R}")
            .ToList();

        Assert.True(
            failures.Count == 0,
            $"Golden int8 vector mismatch on {OperatingSystemDescription()}: " +
            $"min={minimum:R}, mean={mean:R}, required>{Int8CosineMin:R}; " +
            $"below threshold: {string.Join(", ", failures)}.");
    }

    [Fact]
    public void Golden_fixture_covers_required_roles_and_edge_cases()
    {
        var golden = LoadGolden();

        Assert.Equal(12, golden.Cases.Count);
        Assert.Equal(12, golden.Cases.Select(testCase => testCase.Id).Distinct().Count());
        Assert.Equal(6, golden.Cases.Count(testCase => testCase.Role == "query"));
        Assert.Equal(6, golden.Cases.Count(testCase => testCase.Role == "document"));

        foreach (var id in new[]
                 {
                     "ru-query", "ru-document",
                     "en-query", "en-document",
                     "mixed-query", "mixed-document",
                     "long-query", "long-document",
                     "punctuation-query", "punctuation-document",
                     "unicode-query", "unicode-document",
                 })
        {
            Assert.Contains(golden.Cases, testCase => testCase.Id == id);
        }

        var longCase = golden.Cases.Single(testCase => testCase.Id == "long-query");
        Assert.True(longCase.Truncated);
        Assert.Equal(OnnxE5Embedder.MaximumTokens, longCase.UsedTokens);
        Assert.True(longCase.TokenCount > OnnxE5Embedder.MaximumTokens);

        foreach (var testCase in golden.Cases)
        {
            Assert.Equal(OnnxE5Embedder.EmbeddingDimensions, testCase.Vector.Length);
            Assert.Equal(0, testCase.InputIds[0]);
            Assert.Equal(2, testCase.InputIds[^1]);
        }
    }

    [Fact]
    public void Reference_fp32_vectors_are_within_documented_loose_tolerance()
    {
        var golden = LoadGolden();
        var reference = LoadReference();
        var referenceById = reference.Cases.ToDictionary(
            testCase => testCase.Id,
            StringComparer.Ordinal);

        Assert.Equal(golden.Cases.Count, reference.Cases.Count);
        Assert.Equal(0.975, reference.Comparison.Tolerance.CosineMin, 6);

        var similarities = new List<double>();
        foreach (var testCase in golden.Cases)
        {
            if (!referenceById.TryGetValue(testCase.Id, out var referenceCase) ||
                referenceCase is null)
            {
                throw new InvalidOperationException(
                    $"Reference fp32 fixture is missing '{testCase.Id}'.");
            }

            var similarity = Cosine(testCase.Vector, referenceCase.Vector);
            similarities.Add(similarity);
            Assert.True(
                similarity >= reference.Comparison.Tolerance.CosineMin,
                $"Int8/fp32 model-quality floor failed for '{testCase.Id}': " +
                $"cosine={similarity:R}, required>={reference.Comparison.Tolerance.CosineMin:R}. " +
                "This is not a C# pipeline bug: int8 is no longer adequate for this model. " +
                "Re-evaluate int8 vs fp16/fp32 or regenerate the reference; " +
                "do not loosen the C# correctness check.");
        }

        var measuredMin = similarities.Min();
        var measuredMean = similarities.Average();
        Assert.InRange(
            Math.Abs(reference.Comparison.CosineInt8VsFp32.Min - measuredMin),
            0.0,
            1e-12);
        Assert.InRange(
            Math.Abs(reference.Comparison.CosineInt8VsFp32.Mean - measuredMean),
            0.0,
            1e-12);
    }

    private static OnnxE5Embedder CreateEmbedder() =>
        new(new OnnxE5Options
        {
            ModelDirectory = OnnxTestPaths.ModelDirectory!,
        });

    private static string OperatingSystemDescription() =>
        OperatingSystem.IsWindows() ? "Windows" :
        OperatingSystem.IsLinux() ? "Linux" :
        "non-Windows";

    private static string Prefix(string role) => role.ToLowerInvariant() switch
    {
        "query" => "query: ",
        "document" => "passage: ",
        _ => throw new InvalidOperationException($"Unknown golden role '{role}'."),
    };

    private static EmbeddingRole ParseRole(string role) => role.ToLowerInvariant() switch
    {
        "query" => EmbeddingRole.Query,
        "document" => EmbeddingRole.Document,
        _ => throw new InvalidOperationException($"Unknown golden role '{role}'."),
    };

    private static double Norm(IReadOnlyList<double> vector) =>
        Math.Sqrt(vector.Sum(value => value * value));

    private static double Cosine(IReadOnlyList<double> left, IReadOnlyList<double> right)
    {
        Assert.Equal(left.Count, right.Count);

        var dot = 0.0;
        var leftNormSquared = 0.0;
        var rightNormSquared = 0.0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftNormSquared += left[index] * left[index];
            rightNormSquared += right[index] * right[index];
        }

        var denominator = Math.Sqrt(leftNormSquared * rightNormSquared);
        return denominator == 0.0 ? 0.0 : dot / denominator;
    }

    private static GoldenFile LoadGolden() => LoadJson<GoldenFile>("golden_int8.json");

    private static ReferenceFile LoadReference() => LoadJson<ReferenceFile>("reference_fp32.json");

    private static T LoadJson<T>(string fileName)
    {
        var path = OnnxTestPaths.GoldenFixturePath(fileName);
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Could not deserialize '{path}'.");
    }

    private sealed class GoldenFile
    {
        public List<GoldenCase> Cases { get; init; } = [];
    }

    private sealed class GoldenCase
    {
        public string Id { get; init; } = "";
        public string Text { get; init; } = "";
        public string Role { get; init; } = "";

        [JsonPropertyName("input_ids")]
        public long[] InputIds { get; init; } = [];

        [JsonPropertyName("token_count")]
        public int TokenCount { get; init; }

        [JsonPropertyName("used_tokens")]
        public int UsedTokens { get; init; }

        public bool Truncated { get; init; }
        public double[] Vector { get; init; } = [];
    }

    private sealed class ReferenceFile
    {
        public List<ReferenceCase> Cases { get; init; } = [];
        public ReferenceComparison Comparison { get; init; } = new();
    }

    private sealed class ReferenceCase
    {
        public string Id { get; init; } = "";
        public double[] Vector { get; init; } = [];
    }

    private sealed class ReferenceComparison
    {
        [JsonPropertyName("cosine_int8_vs_fp32")]
        public CosineComparison CosineInt8VsFp32 { get; init; } = new();

        public ReferenceTolerance Tolerance { get; init; } = new();
    }

    private sealed class CosineComparison
    {
        public double Min { get; init; }
        public double Mean { get; init; }
    }

    private sealed class ReferenceTolerance
    {
        [JsonPropertyName("cosine_min")]
        public double CosineMin { get; init; }
    }
}

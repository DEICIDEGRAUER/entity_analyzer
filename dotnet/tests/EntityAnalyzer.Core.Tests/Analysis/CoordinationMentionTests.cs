using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Variants;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Regression guard for context-window deduplication: two different entities
/// in one sentence must produce two mentions, not one.
/// </summary>
public sealed class CoordinationMentionTests : IDisposable
{
    private readonly string _corpus = Path.Combine(
        Path.GetTempPath(), "ea-coordination-" + Guid.NewGuid().ToString("N"));

    private readonly string _out = Path.Combine(
        Path.GetTempPath(), "ea-coordination-out-" + Guid.NewGuid().ToString("N"));

    public CoordinationMentionTests()
    {
        Directory.CreateDirectory(_corpus);
        File.WriteAllText(
            Path.Combine(_corpus, "coordination.txt"),
            "Лев Толстой и Александр Пушкин беседовали в библиотеке.",
            new System.Text.UTF8Encoding(false));
    }

    public void Dispose()
    {
        try { Directory.Delete(_corpus, true); } catch { }
        try { Directory.Delete(_out, true); } catch { }
    }

    [Fact]
    public void Separate_entities_in_one_sentence_both_survive()
    {
        var options = new AnalyzerOptions();
        options.Corpus.Dir = _corpus;
        options.Output.Dir = _out;
        options.Cache.Dir = Path.Combine(_out, ".cache");
        Presets.Apply(Presets.Resolve("minimal"), options.Processing);

        var catalog = new EntityCatalog(
        [
            new EntityDefinition("Толстой", "Tolstoy", ["Толстой", "Лев Толстой"], []),
            new EntityDefinition("Пушкин", "Pushkin", ["Пушкин", "Александр Пушкин"], []),
        ]);

        var session = new AnalysisEngine(options, catalog, presetName: "minimal")
            .Run(CancellationToken.None);

        var mentions = session.Files.Single(f => f.FileName == "coordination.txt").Mentions;

        Assert.Contains(mentions, m => m.Entity == "Толстой");
        Assert.Contains(mentions, m => m.Entity == "Пушкин");
    }
}

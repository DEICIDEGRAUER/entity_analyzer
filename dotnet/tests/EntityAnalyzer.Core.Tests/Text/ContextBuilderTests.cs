using EntityAnalyzer.Core.Text;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

public class ContextBuilderTests
{
    // ---------- CalculateContextSize (defaults 75 / 25 / threshold 50) ----------

    [Theory]
    [InlineData(0, 75)]
    [InlineData(3, 75)]
    [InlineData(5, 75)]
    [InlineData(6, 72)]
    [InlineData(10, 62)]
    [InlineData(15, 50)]
    [InlineData(16, 49)]
    [InlineData(50, 25)]
    [InlineData(120, 25)]
    public void Context_size_shrinks_piecewise_with_mention_count(int total, int expected)
        => Assert.Equal(expected, ContextBuilder.CalculateContextSize(total));

    // ---------- SelectWindowIndices ----------

    private static readonly string Text =
        "Первое предложение тут. " +                       // 3 words
        "Второе предложение тоже здесь стоит. " +           // 5 words
        "Третья фраза содержит заветное имя Йеб прямо в себе. " +
        "Четвёртое предложение болтается рядом без нужды. " +
        "Пятое завершает текст окончательно.";

    private static readonly List<SentenceSpan> Spans = SentenceSplitter.SplitWithSpans(Text);

    [Fact]
    public void Window_contains_sentence_with_the_match()
    {
        var idx = Text.IndexOf("Йеб", StringComparison.Ordinal);
        var window = ContextBuilder.SelectWindowIndices(Spans, idx, idx + 3, maxWords: 60);
        var joined = string.Join(" ", window.Select(i => Text.Substring(Spans[i].Start, Spans[i].End - Spans[i].Start)));
        Assert.Contains("заветное имя Йеб", joined);
    }

    [Fact]
    public void Budget_limits_neighbors_on_both_sides()
    {
        var idx = Text.IndexOf("Йеб", StringComparison.Ordinal);
        var targetIdx = Spans.FindIndex(s => Text.Substring(s.Start, s.End - s.Start).Contains("Йеб"));
        var window = ContextBuilder.SelectWindowIndices(Spans, idx, idx + 3, maxWords: 8); // 45% ≈ 3.6 words per side
        // With such a tight budget only the target sentence plus maybe one short neighbor fit.
        Assert.True(window.Count < Spans.Count);
        Assert.Contains(targetIdx, window);
    }

    [Fact]
    public void Generous_budget_returns_all_sentences()
    {
        var idx = Text.IndexOf("Йеб", StringComparison.Ordinal);
        var window = ContextBuilder.SelectWindowIndices(Spans, idx, idx + 3, maxWords: 200);
        Assert.Equal(Spans.Count, window.Count);
    }

    // ---------- Build ----------

    [Fact]
    public void Built_context_includes_match_and_neighbors()
    {
        var idx = Text.IndexOf("Йеб", StringComparison.Ordinal);
        var ctx = ContextBuilder.Build(Text, Spans, idx, idx + 3, maxWords: 60);
        Assert.Contains("Йеб", ctx);
        Assert.Contains("Первое предложение", ctx);
        Assert.Contains("Пятое завершает", ctx);
        Assert.DoesNotContain("  ", ctx); // whitespace normalized
    }

    [Fact]
    public void Tight_context_keeps_at_least_the_target_sentence()
    {
        var idx = Text.IndexOf("Йеб", StringComparison.Ordinal);
        var ctx = ContextBuilder.Build(Text, Spans, idx, idx + 3, maxWords: 4);
        Assert.Contains("заветное имя Йеб", ctx);
    }

    // ---------- Deduplicate ----------

    private record Item(int Id, string Ctx);

    [Fact]
    public void Deduplicate_ignores_whitespace_differences()
    {
        var items = new List<Item>
        {
            new(1, "Наг и Йеб пришли"),
            new(2, "Наг и Йеб\nпришли"),
            new(3, "Совершенно другой контекст"),
        };
        var result = ContextBuilder.Deduplicate(items, i => i.Ctx);
        Assert.Equal(new[] { 1, 3 }, result.Select(i => i.Id));
    }

    private record EntityItem(int Id, string Entity, string Ctx);

    [Fact]
    public void Deduplicate_is_scoped_to_entity_when_entity_selector_is_supplied()
    {
        var items = new List<EntityItem>
        {
            new(1, "Толстой", "Толстой и Пушкин беседовали"),
            new(2, "Пушкин", "Толстой и Пушкин беседовали"),
            new(3, "Толстой", "Толстой и Пушкин беседовали"),
        };

        var result = ContextBuilder.Deduplicate(items, i => i.Ctx, i => i.Entity);

        Assert.Equal(new[] { 1, 2 }, result.Select(i => i.Id));
    }
}

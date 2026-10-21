using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R2-1 «Шахтёр коллокаций»: устойчивые биграммы вокруг упоминаний сущности
/// («воющей собаки» рядом с «Наг») → ранжированные кандидаты для lexicon-
/// словарей паттернов. Топливо для Кодекс-скаута (R1-1).
/// </summary>
public class CollocationMinerTests
{
    private const string Nag = "Наг";

    [Fact]
    public void Empty_texts_give_no_collocations()
    {
        Assert.Empty(CollocationMiner.Mine([], Nag));
    }

    [Fact]
    public void Bigram_next_to_entity_is_found()
    {
        var text = Text.TextNormalizer.Normalize("Воющей собаки Наг ждал у моря.");
        var result = CollocationMiner.Mine([text], Nag);
        Assert.Contains(result, c => c.Phrase.Contains("собаки", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Frequency_is_counted_across_occurrences()
    {
        var t1 = Text.TextNormalizer.Normalize("Пески Арракис поглотили караван.");
        var t2 = Text.TextNormalizer.Normalize("Ветра Арракис не щадят никого.");
        var result = CollocationMiner.Mine([t1, t2], "Арракис");
        var sands = result.FirstOrDefault(c => c.Phrase.Contains("пески", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(sands);
        Assert.Equal(1, sands.Count); // «пески» один раз
    }

    [Fact]
    public void Repeated_bigram_ranks_first()
    {
        var texts = new[]
        {
            Text.TextNormalizer.Normalize("Жрец Нага шептал."),
            Text.TextNormalizer.Normalize("Жрец Нага молился."),
            Text.TextNormalizer.Normalize("Старый жрец Нага усмехнулся."),
        };
        var result = CollocationMiner.Mine(texts, "Нага");
        Assert.True(result.Count > 0);
        // Регистр сохраняется как в тексте (капитализация — сигнал для скаута).
        Assert.Equal("Жрец нага", result[0].Phrase); // самая частотная
    }

    [Fact]
    public void Short_words_are_ignored()
    {
        var text = Text.TextNormalizer.Normalize("У Наг он ждал.");
        var result = CollocationMiner.Mine([text], Nag);
        Assert.DoesNotContain(result, c => c.Phrase.Split(' ').Any(w => w.Length < 3 && w != Nag.ToLowerInvariant()));
    }

    [Fact]
    public void Top_limit_is_respected()
    {
        var text = Text.TextNormalizer.Normalize(
            "Наг шёл через лес, Наг видел реку, Наг слышал ветер, Наг помнил горы.");
        var result = CollocationMiner.Mine([text], Nag, top: 2);
        Assert.True(result.Count <= 2);
    }
}

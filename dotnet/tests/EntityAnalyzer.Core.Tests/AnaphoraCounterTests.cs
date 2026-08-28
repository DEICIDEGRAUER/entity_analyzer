using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R2-4 «Анафора-режим»: после явного упоминания ближайшие местоимения
/// («он/она/оно/они») в окне предложений засчитываются как косвенные
/// упоминания той же сущности. Честная «плотность присутствия»; слой
/// выключен по умолчанию ради воспроизводимости старых прогонов.
/// </summary>
public class AnaphoraCounterTests
{
    private const string Text =
        "Жрец Ктулху шептал у алтаря. Он был бледен. Город спал. " +
        "Нага боялись все. Она приходила в тумане.";

    private static List<Mention> Explicit() =>
    [
        new Mention(1, "Ктулху", "Ктулху", 1, "Жрец Ктулху шептал", Offset: 5),
        new Mention(2, "Наг и Йеб", "Нага", 1, "Нага боялись все", Offset: 62),
    ];

    [Fact]
    public void No_explicit_mentions_gives_no_hits()
    {
        Assert.Empty(AnaphoraCounter.Count(EntityAnalyzer.Core.Text.TextNormalizer.Normalize("Он шёл. Она пела."), []));
    }

    [Fact]
    public void Pronoun_after_mention_is_counted()
    {
        var hits = AnaphoraCounter.Count(EntityAnalyzer.Core.Text.TextNormalizer.Normalize(Text), Explicit());
        Assert.Contains(hits, h => h.ResolvedEntity == "Ктулху" && h.Pronoun == "он");
    }

    [Fact]
    public void Binding_goes_to_nearest_previous_mention()
    {
        var hits = AnaphoraCounter.Count(EntityAnalyzer.Core.Text.TextNormalizer.Normalize(Text), Explicit());
        // «Она» после «Нага боялись все» → Наг и Йеб.
        var she = hits.FirstOrDefault(h => h.Pronoun == "она");
        Assert.NotNull(she);
        Assert.Equal("Наг и Йеб", she.ResolvedEntity);
    }

    [Fact]
    public void Window_limits_distant_pronouns()
    {
        // «Город спал.» между — третье предложение после «Ктулху».
        var hits = AnaphoraCounter.Count(
            EntityAnalyzer.Core.Text.TextNormalizer.Normalize(Text), Explicit(), windowSentences: 1);
        // При окне 1 предложение «Он был бледен» (следующее) ещё ловится,
        // но дальние местоимения — нет.
        Assert.All(hits.Where(h => h.ResolvedEntity == "Ктулху"),
            h => Assert.True(h.SentenceIndex <= 1));
    }

    [Fact]
    public void Non_pronoun_words_are_ignored()
    {
        var text = EntityAnalyzer.Core.Text.TextNormalizer.Normalize("Ктулху спит. Город спит.");
        var hits = AnaphoraCounter.Count(text,
            [new Mention(1, "Ктулху", "Ктулху", 1, "к", Offset: 0)]);
        Assert.Empty(hits); // «город» — не местоимение
    }
}

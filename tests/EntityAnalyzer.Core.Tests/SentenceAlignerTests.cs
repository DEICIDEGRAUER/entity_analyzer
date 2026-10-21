using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R2-25 «Выравнивание параллельных текстов»: карта соответствий RU↔EN
/// по предложениям — длина + числа-якоря + совпадение имён через
/// существующую транслитерацию. Для переводчиков: «как это названо
/// в английском издании».
/// </summary>
public class SentenceAlignerTests
{
    private const string Ru =
        "Жрец Ктулху шептал у алтаря. Город спал в тумане. " +
        "В 1928 году экспедиция уехала. Нага боялись все моряки.";

    private const string En =
        "The priest of Cthulhu whispered at the altar. The city slept in fog. " +
        "In 1928 the expedition departed. All sailors feared Naga.";

    [Fact]
    public void Empty_inputs_give_no_pairs()
    {
        var pairs = SentenceAligner.Align([], []);
        Assert.Empty(pairs);
    }

    [Fact]
    public void Equal_count_texts_align_one_to_one()
    {
        var ru = SentenceAligner.SplitSentences(Ru);
        var en = SentenceAligner.SplitSentences(En);
        Assert.Equal(4, ru.Count);

        var pairs = SentenceAligner.Align(ru, en);
        Assert.Equal(4, pairs.Count);
        Assert.All(pairs, p => Assert.Equal(p.RuIndex, p.EnIndex));
    }

    [Fact]
    public void Number_anchor_binds_sentences_with_same_number()
    {
        var ru = SentenceAligner.SplitSentences(Ru);
        var en = SentenceAligner.SplitSentences(En);
        var pairs = SentenceAligner.Align(ru, en);

        // Предложение с «1928» выровнено с предложением, где тоже «1928».
        var pair = pairs.First(p => p.RuText.Contains("1928"));
        Assert.Contains("1928", pair.EnText);
    }

    [Fact]
    public void Name_anchor_uses_transliteration()
    {
        var ru = SentenceAligner.SplitSentences(Ru);
        var en = SentenceAligner.SplitSentences(En);
        var pairs = SentenceAligner.Align(ru, en);

        // «Ктулху» ↔ «Cthulhu» через транслитерацию.
        var pair = pairs.First(p => p.RuText.Contains("Ктулху"));
        Assert.Contains("Cthulhu", pair.EnText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Different_counts_still_align_monotonically()
    {
        var ru = SentenceAligner.SplitSentences("Один. Два. Три. Четыре. Пять.");
        var en = SentenceAligner.SplitSentences("One two. Three four.");
        var pairs = SentenceAligner.Align(ru, en);
        // Монотонность: индексы EN не убывают.
        var enIdx = pairs.Select(p => p.EnIndex).ToList();
        Assert.Equal(enIdx.OrderBy(x => x), enIdx);
    }
}

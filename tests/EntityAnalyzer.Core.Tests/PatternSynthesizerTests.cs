using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R1-23 «Генератор паттернов из примеров»: 3–5 предложений с формами
/// целевого имени → черновик JSON-паттерна (варианты + характерное
/// окружение) для правки в редакторе. Эвристика офлайн, без LLM.
/// </summary>
public class PatternSynthesizerTests
{
    private static readonly string[] Examples =
    [
        "Жрец Ктулху шептал у алтаря.",
        "Сон Ктулху тревожил город.",
        "Имя Ктулху запрещалось.",
        "Жрецы Ктулху собрались у моря.",
    ];

    [Fact]
    public void Empty_examples_give_empty_draft()
    {
        var draft = PatternSynthesizer.FromExamples([], "Ктулху");
        Assert.Empty(draft.Variants);
        Assert.Empty(draft.ContextWords);
    }

    [Fact]
    public void Target_itself_is_a_variant()
    {
        var draft = PatternSynthesizer.FromExamples(Examples, "Ктулху");
        Assert.Contains("Ктулху", draft.Variants);
    }

    [Fact]
    public void Same_prefix_words_become_variants()
    {
        // «Ктулху» и «Ктулхув» (выдуманная форма) — общий префикс.
        var examples = new[] { "Зов Ктулху слышен.", "Печать Ктулхув горит." };
        var draft = PatternSynthesizer.FromExamples(examples, "Ктулху");
        Assert.Contains("Ктулху", draft.Variants);
        Assert.Contains("Ктулхув", draft.Variants);
    }

    [Fact]
    public void Frequent_neighbor_word_enters_context()
    {
        // «жрец» рядом с целью в двух примерах.
        var examples = new[] { "Жрец Ктулху шептал.", "Жрец Ктулху ждал.", "Море бушевало." };
        var draft = PatternSynthesizer.FromExamples(examples, "Ктулху");
        Assert.Contains(draft.ContextWords, w => w.Equals("жрец", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Rare_words_do_not_enter_context()
    {
        var draft = PatternSynthesizer.FromExamples(Examples, "Ктулху");
        // «алтаря», «тревожил», «запрещалось» — каждое только в одном примере.
        Assert.DoesNotContain(draft.ContextWords, w => w.Equals("алтаря", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(draft.ContextWords, w => w.Equals("тревожил", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ToJson_produces_pattern_skeleton()
    {
        var draft = PatternSynthesizer.FromExamples(Examples, "Ктулху");
        var json = PatternSynthesizer.ToJson(draft);
        Assert.Contains("\"name\"", json);
        Assert.Contains("Ктулху", json);
        Assert.Contains("any_of", json);
    }
}

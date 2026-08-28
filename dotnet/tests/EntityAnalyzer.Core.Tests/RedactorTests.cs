using EntityAnalyzer.Core.Redaction;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R2-22 «Режим редакции»: обезличенная копия текста — найденные упоминания
/// заменяются плейсхолдерами [ENTITY_N]; одна сущность = один плейсхолдер;
/// карта соответствий возвращается отдельно. Пересекающиеся диапазоны:
/// первый побеждает, последующие пропускаются.
/// </summary>
public class RedactorTests
{
    [Fact]
    public void Single_mention_is_replaced_with_placeholder()
    {
        var text = "Жрец Ктулху шептал у алтаря.";
        var (redacted, map) = Redactor.Apply(text, [(6, 6, "Ктулху")]);
        Assert.Contains("[ENTITY_1]", redacted);
        Assert.DoesNotContain("Ктулху", redacted);
        var e = Assert.Single(map);
        Assert.Equal("Ктулху", e.Original);
        Assert.Equal("[ENTITY_1]", e.Placeholder);
    }

    [Fact]
    public void Same_entity_reuses_one_placeholder()
    {
        var text = "Ктулху спит. Ктулху ждёт.";
        var (redacted, map) = Redactor.Apply(text, [(0, 6, "Ктулху"), (13, 6, "Ктулху")]);
        Assert.Single(map);
        Assert.Equal("[ENTITY_1] спит. [ENTITY_1] ждёт.", redacted);
    }

    [Fact]
    public void Different_entities_get_different_placeholders()
    {
        var text = "Нага и Йеб";
        var (redacted, map) = Redactor.Apply(text, [(0, 4, "Наг и Йеб"), (7, 3, "Йеб")]);
        Assert.Equal(2, map.Count); // две разные сущности — два плейсхолдера
        Assert.NotEqual(map[0].Placeholder, map[1].Placeholder);
    }

    [Fact]
    public void Overlapping_spans_first_wins()
    {
        var text = "Наг и Йеб";
        // «Наг и Йеб» (0..9) перекрывает «Йеб» (7..10).
        var (redacted, map) = Redactor.Apply(text, [(0, 9, "Наг и Йеб"), (7, 3, "Йеб")]);
        Assert.Equal("[ENTITY_1]", redacted);
        Assert.Single(map);
    }

    [Fact]
    public void Out_of_range_spans_are_ignored_safely()
    {
        var text = "Текст.";
        var (redacted, map) = Redactor.Apply(text, [(100, 5, "X"), (-3, 2, "Y")]);
        Assert.Equal("Текст.", redacted);
        Assert.Empty(map);
    }

    [Fact]
    public void Empty_input_gives_empty_map()
    {
        var (_, map) = Redactor.Apply("", Array.Empty<(int, int, string)>());
        Assert.Empty(map);
    }
}

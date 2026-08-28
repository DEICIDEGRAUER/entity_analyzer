using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Analysis;

// ═══════════════ R1-19 «Живой статус достоверности» ═══════════════
// Бейдж уверенности упоминания из доступных сигналов:
//   • точное совпадение по словарю → High;
//   • нечёткое (IsFuzzy + «[fuzzy:N%]» в варианте): N ≥ 85 → Medium, иначе Low;
//   • нечёткое без распознанного процента → Low (защитный сценарий);
//   • повторы (Repeats > 1) добавляют причину, уровень не поднимают.
// Порча/морфология входят в словарь вариантов и пост-фактум неотличимы
// от точного совпадения — расширение зарезервировано за движком.

public class ConfidenceBadgeTests
{
    private static Mention Exact(int repeats = 1) =>
        new(1, "Наг и Йеб", "Нага", 3, "контекст", Repeats: repeats);

    private static Mention Fuzzy(string variant) =>
        new(1, "Наг и Йеб", variant, 3, "контекст", IsFuzzy: true);

    [Fact]
    public void Exact_match_is_high()
    {
        var badge = ConfidenceBadge.From(Exact());
        Assert.Equal(ConfidenceLevel.High, badge.Level);
        Assert.Equal("высокая", badge.Label);
        Assert.Contains(badge.Reasons, r => r.Contains("точное"));
    }

    [Fact]
    public void Fuzzy_high_score_is_medium()
    {
        var badge = ConfidenceBadge.From(Fuzzy("Наг и Йеб [fuzzy:92%]"));
        Assert.Equal(ConfidenceLevel.Medium, badge.Level);
        Assert.Equal("средняя", badge.Label);
        Assert.Contains(badge.Reasons, r => r.Contains("92%"));
    }

    [Fact]
    public void Fuzzy_low_score_is_low()
    {
        var badge = ConfidenceBadge.From(Fuzzy("Наг и Йеб [fuzzy:61%]"));
        Assert.Equal(ConfidenceLevel.Low, badge.Level);
        Assert.Equal("низкая", badge.Label);
    }

    [Fact]
    public void Fuzzy_without_score_falls_back_to_low()
    {
        var badge = ConfidenceBadge.From(new Mention(
            1, "Наг и Йеб", "Нага", 3, "контекст", IsFuzzy: true));
        Assert.Equal(ConfidenceLevel.Low, badge.Level);
    }

    [Theory]
    [InlineData(84, ConfidenceLevel.Low)]
    [InlineData(85, ConfidenceLevel.Medium)]
    public void Score_boundary_is_inclusive_for_medium(int score, ConfidenceLevel expected)
    {
        var badge = ConfidenceBadge.From(Fuzzy($"Наг и Йеб [fuzzy:{score}%]"));
        Assert.Equal(expected, badge.Level);
    }

    [Fact]
    public void Repeats_add_reason_without_raising_level()
    {
        var badge = ConfidenceBadge.From(Exact(repeats: 3));
        Assert.Equal(ConfidenceLevel.High, badge.Level);
        Assert.Contains(badge.Reasons, r => r.Contains("повторы") && r.Contains('3'));
    }

    [Fact]
    public void Suffix_is_empty_for_high_and_explains_lower_levels()
    {
        // Высокая достоверность не шумит в интерфейсе.
        Assert.Equal(string.Empty, ConfidenceBadge.From(Exact()).ToSuffix());

        var medium = ConfidenceBadge.From(Fuzzy("Наг и Йеб [fuzzy:90%]"));
        var suffix = medium.ToSuffix();
        Assert.Contains("достоверность", suffix);
        Assert.Contains("средняя", suffix);
        Assert.Contains("90%", suffix);
    }
}

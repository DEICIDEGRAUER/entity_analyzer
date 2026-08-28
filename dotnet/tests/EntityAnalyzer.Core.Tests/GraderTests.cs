using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R2-28 «Classroom-режим»: проверка лабораторных — работа студента
/// (.earepro) сверяется с эталоном преподавателя по множеству найденных
/// сущностей и числам прогона. Score = 0.5·полнота + 0.5·точность.
/// </summary>
public class GraderTests
{
    private static RunRecord Make(Dictionary<string, int> entities, int total) => new()
    {
        Preset = "light",
        TotalMentions = total,
        UniqueEntities = entities.Count,
        EntityCounts = entities,
    };

    [Fact]
    public void Identical_runs_pass_with_full_score()
    {
        var reference = Make(new Dictionary<string, int> { ["Ктулху"] = 7, ["Наг и Йеб"] = 3 }, 10);
        var submission = Make(new Dictionary<string, int> { ["Ктулху"] = 7, ["Наг и Йеб"] = 3 }, 10);

        var result = Grader.Compare(reference, submission);
        Assert.True(result.Passed);
        Assert.Equal(1.0, result.Score, precision: 3);
    }

    [Fact]
    public void Missing_entity_lowers_recall_and_fails()
    {
        var reference = Make(new Dictionary<string, int> { ["Ктулху"] = 7, ["Наг и Йеб"] = 3 }, 10);
        var submission = Make(new Dictionary<string, int> { ["Ктулху"] = 7 }, 7);

        var result = Grader.Compare(reference, submission);
        Assert.False(result.Passed); // «Наг и Йеб» не найден
        Assert.Contains(result.Notes, n => n.Contains("Наг и Йеб"));
    }

    [Fact]
    public void Extra_entities_lower_precision()
    {
        var reference = Make(new Dictionary<string, int> { ["Ктулху"] = 7 }, 7);
        var submission = Make(new Dictionary<string, int>
        {
            ["Ктулху"] = 7,
            ["Азатот"] = 2,
            ["Шуб-Ниггурат"] = 1,
        }, 10);

        var result = Grader.Compare(reference, submission);
        // Точность 1/3 → Score = 0.5·1 + 0.5·(1/3) ≈ 0.667 < порога.
        Assert.True(result.Score < 0.9);
        Assert.False(result.Passed);
        Assert.Contains(result.Notes, n => n.Contains("Азатот"));
    }

    [Fact]
    public void Mention_count_within_tolerance_is_noted_not_failing()
    {
        var reference = Make(new Dictionary<string, int> { ["Ктулху"] = 100 }, 100);
        var submission = Make(new Dictionary<string, int> { ["Ктулху"] = 105 }, 105);

        var result = Grader.Compare(reference, submission, tolerance: 0.1);
        Assert.True(result.Passed); // +5% в допуске ±10%
    }

    [Fact]
    public void Empty_submission_fails_cleanly()
    {
        var reference = Make(new Dictionary<string, int> { ["Ктулху"] = 7 }, 7);
        var submission = Make([], 0);

        var result = Grader.Compare(reference, submission);
        Assert.False(result.Passed);
        Assert.Equal(0.0, result.Score);
    }
}

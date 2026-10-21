using EntityAnalyzer.Core.Persistence;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>Вердикт проверки лабораторной работы.</summary>
public sealed record GradeResult(
    bool Passed,
    double Score,
    double Recall,
    double Precision,
    IReadOnlyList<string> Notes);

/// <summary>
/// R2-28 «Classroom-режим»: проверка лабораторных — работа студента
/// (.earepro) сверяется с эталоном преподавателя. Критерии: полнота
/// (доля эталонных сущностей, найденных студентом), точность (доля сущностей
/// студента, входящих в эталон); Score = 0.5·полнота + 0.5·точность.
/// Числа упоминаний сверяются с допуском (по умолчанию ±10%) — расхождение
/// в допуске отмечается, но не валит работу.
/// </summary>
public static class Grader
{
    private const double PassThreshold = 0.9;

    public static GradeResult Compare(RunRecord reference, RunRecord submission, double tolerance = 0.10)
    {
        var notes = new List<string>();
        var refEntities = reference.EntityCounts?.Keys.ToHashSet(StringComparer.Ordinal) ?? [];
        var subEntities = submission.EntityCounts?.Keys.ToHashSet(StringComparer.Ordinal) ?? [];

        var missed = refEntities.Except(subEntities).OrderBy(n => n, StringComparer.Ordinal).ToList();
        var extra = subEntities.Except(refEntities).OrderBy(n => n, StringComparer.Ordinal).ToList();

        var recall = refEntities.Count == 0 ? 1.0 : (double)(refEntities.Count - missed.Count) / refEntities.Count;
        var precision = subEntities.Count == 0 ? 0.0 : (double)(subEntities.Count - extra.Count) / subEntities.Count;
        var score = 0.5 * recall + 0.5 * precision;

        foreach (var name in missed)
            notes.Add($"Не найдена эталонная сущность: {name}");
        foreach (var name in extra)
            notes.Add($"Лишняя сущность (нет в эталоне): {name}");

        if (reference.TotalMentions > 0)
        {
            var delta = Math.Abs(submission.TotalMentions - reference.TotalMentions) /
                        (double)reference.TotalMentions;
            if (delta > tolerance)
                notes.Add($"Упоминаний: {submission.TotalMentions} против эталонных {reference.TotalMentions} " +
                          $"(отклонение {delta:P0} превышает допуск {tolerance:P0})");
            else if (submission.TotalMentions != reference.TotalMentions)
                notes.Add($"Упоминаний: {submission.TotalMentions} против эталонных {reference.TotalMentions} " +
                          $"(в допуске {tolerance:P0})");
        }

        // Пропущенная эталонная сущность — безусловный незачёт лабораторной.
        var passed = missed.Count == 0 && score >= PassThreshold;
        return new GradeResult(passed, score, recall, precision, notes);
    }
}

using EntityAnalyzer.Core.Persistence;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>Дельта одной сущности между двумя прогонами (R1-3).</summary>
public sealed record EntityDelta(string Entity, int Before, int After)
{
    /// <summary>Изменение: +новые упоминания, −пропавшие.</summary>
    public int Delta => After - Before;
}

/// <summary>Результат позиционного сравнения двух прогонов.</summary>
public sealed record RunComparison(
    IReadOnlyList<EntityDelta> Entities,
    IReadOnlyList<EntityDelta> Appeared,
    IReadOnlyList<EntityDelta> Disappeared,
    int TotalBefore,
    int TotalAfter)
{
    public int TotalDelta => TotalAfter - TotalBefore;
}

/// <summary>
/// Дифф-движок прогонов (R1-3): позиционное сравнение словарей EntityCounts
/// из журнала истории. Отвечает на исследовательский вопрос «что изменилось
/// между прогонами»: дельты по каждой сущности, появления и исчезновения.
/// Чистая функция без состояния — потребители: страница «История», CLI.
/// </summary>
public static class RunComparer
{
    public static RunComparison Compare(
        IReadOnlyDictionary<string, int> before,
        IReadOnlyDictionary<string, int> after)
    {
        var entities = new List<EntityDelta>();
        var appeared = new List<EntityDelta>();
        var disappeared = new List<EntityDelta>();

        foreach (var name in before.Keys.Union(after.Keys).Distinct(StringComparer.Ordinal))
        {
            var b = before.GetValueOrDefault(name);
            var a = after.GetValueOrDefault(name);
            var delta = new EntityDelta(name, b, a);
            entities.Add(delta);

            if (b == 0 && a > 0) appeared.Add(delta);
            else if (a == 0 && b > 0) disappeared.Add(delta);
        }

        // Ранжирование по силе изменения: |Δ| убыв., при равенстве — алфавит.
        entities.Sort((x, y) =>
        {
            var byMagnitude = Math.Abs(y.Delta).CompareTo(Math.Abs(x.Delta));
            return byMagnitude != 0 ? byMagnitude : string.Compare(x.Entity, y.Entity, StringComparison.Ordinal);
        });

        return new RunComparison(entities, appeared, disappeared,
            before.Values.Sum(), after.Values.Sum());
    }

    /// <summary>
    /// UX-тикет «база сравнения»: единое правило выбора базового прогона диффа.
    /// Список хранится новыми вперёд; база — самый свежий прогон, не совпадающий
    /// с выбранным. null — сравнение невозможно (&lt;2 прогонов) либо нечего
    /// подсказать. Единственный источник правила: и подсказка до нажатия
    /// «Сравнить», и само сравнение используют только его.
    /// </summary>
    public static RunRecord? PickBase(IReadOnlyList<RunRecord> runsNewestFirst, RunRecord? selected)
    {
        if (runsNewestFirst.Count < 2) return null;
        foreach (var r in runsNewestFirst)
            if (!ReferenceEquals(r, selected))
                return r;
        return null;
    }
}

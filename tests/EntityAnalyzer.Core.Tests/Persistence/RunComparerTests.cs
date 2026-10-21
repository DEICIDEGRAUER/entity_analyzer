using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

// ═══════════════ R1-3: Дифф-движок прогонов ═══════════════
// Позиционное сравнение двух прогонов по словарям EntityCounts из журнала:
// дельты по каждой сущности, появления и исчезновения. Чистая функция —
// детерминирована, покрывается юнит-тестами, потребляется страницей «История».

public class RunComparerTests
{
    private static Dictionary<string, int> Counts(params (string Name, int N)[] items) =>
        items.ToDictionary(x => x.Name, x => x.N);

    [Fact]
    public void Deltas_are_computed_per_entity()
    {
        var before = Counts(("Наг и Йеб", 10), ("Азатот", 4));
        var after = Counts(("Наг и Йеб", 7), ("Азатот", 6));

        var diff = RunComparer.Compare(before, after);

        var nag = diff.Entities.Single(e => e.Entity == "Наг и Йеб");
        Assert.Equal(10, nag.Before);
        Assert.Equal(7, nag.After);
        Assert.Equal(-3, nag.Delta);

        Assert.Equal(+2, diff.Entities.Single(e => e.Entity == "Азатот").Delta);
    }

    [Fact]
    public void Appeared_and_disappeared_entities_are_marked()
    {
        var before = Counts(("Старая", 5));
        var after = Counts(("Новая", 8));

        var diff = RunComparer.Compare(before, after);

        Assert.Equal(0, diff.Entities.Single(e => e.Entity == "Новая").Before);
        Assert.Equal(0, diff.Entities.Single(e => e.Entity == "Старая").After);
        Assert.Contains(diff.Appeared, e => e.Entity == "Новая");
        Assert.Contains(diff.Disappeared, e => e.Entity == "Старая");
    }

    [Fact]
    public void Sorted_by_absolute_delta_then_name()
    {
        var before = Counts(("Б", 10), ("В", 3), ("А", 10));
        var after = Counts(("Б", 14), ("В", 1), ("А", 7));

        var diff = RunComparer.Compare(before, after);

        // |Δ|: Б=4, А=3, В=2 — при равенстве алфавит.
        Assert.Equal(["Б", "А", "В"], diff.Entities.Select(e => e.Entity).ToArray());
    }

    [Fact]
    public void Totals_are_summarized()
    {
        var before = Counts(("А", 5), ("Б", 2));
        var after = Counts(("А", 5));

        var diff = RunComparer.Compare(before, after);

        Assert.Equal(7, diff.TotalBefore);
        Assert.Equal(5, diff.TotalAfter);
        Assert.Equal(-2, diff.TotalDelta);
    }

    [Fact]
    public void Empty_sides_are_handled()
    {
        var empty = new Dictionary<string, int>();
        var some = Counts(("А", 3));

        var first = RunComparer.Compare(empty, some);
        Assert.Equal(3, first.TotalDelta);
        Assert.Empty(first.Disappeared);

        var second = RunComparer.Compare(some, empty);
        Assert.Equal(-3, second.TotalDelta);
        Assert.Single(second.Disappeared);
    }

    [Fact]
    public void Unchanged_entities_present_with_zero_delta()
    {
        var before = Counts(("А", 4));
        var after = Counts(("А", 4));

        var diff = RunComparer.Compare(before, after);

        Assert.Equal(0, diff.Entities.Single().Delta);
        Assert.Empty(diff.Appeared);
        Assert.Empty(diff.Disappeared);
    }

    // ─────────────── UX-тикет: явная база сравнения ───────────────
    // Список прогонов хранится новыми вперёд; база диффа — самый свежий
    // прогон, не совпадающий с выбранным. Раньше правило жило только внутри
    // Compare() страницы «История», и пользователь не видел, с чем сравнит.

    [Fact]
    public void PickBase_returns_newest_run_other_than_selected()
    {
        var a = new RunRecord { StartedAt = DateTimeOffset.Parse("2026-08-26T10:00:00Z") };
        var b = new RunRecord { StartedAt = DateTimeOffset.Parse("2026-08-25T10:00:00Z") };
        var c = new RunRecord { StartedAt = DateTimeOffset.Parse("2026-08-24T10:00:00Z") };

        // Выбран средний — база самый новый.
        Assert.Same(a, RunComparer.PickBase([a, b, c], b));
        // Выбран самый новый — база следующий за ним.
        Assert.Same(b, RunComparer.PickBase([a, b, c], a));
        // Выбран самый старый — база самый новый.
        Assert.Same(a, RunComparer.PickBase([a, b, c], c));
    }

    [Fact]
    public void PickBase_requires_two_runs_and_selection()
    {
        var a = new RunRecord();
        var b = new RunRecord();

        Assert.Null(RunComparer.PickBase([], null));
        Assert.Null(RunComparer.PickBase([a], a));
        // Один прогон, ничего не выбрано — базы нет.
        Assert.Null(RunComparer.PickBase([a], null));
        // Два прогона без выбора — база самый свежий (для подсказки до выбора).
        Assert.Same(a, RunComparer.PickBase([a, b], null));
    }
}

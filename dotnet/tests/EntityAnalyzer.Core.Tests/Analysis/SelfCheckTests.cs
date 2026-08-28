using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Analysis;

// ═══════════════ R2-16 / R1-15: Самопроверка релиза при старте ═══════════════
// Первый запуск новой версии гоняет встроенный мини-корпус (тексты демо-корпуса
// R2-18) со сверкой эталона Resources/selfcheck_baseline.json. Контракт:
//   Verify      — чистая функция сверки «эталон ↔ факт» (все виды расхождений);
//   RunEngine   — детерминированный прогон на зафиксированных опциях;
//   Run         — оркестрация с явной или встроенной базой.
// Опции прогона зафиксированы кодом, а не пресетом: правка пресетов не должна
// ломать эталон — он меняется только осознанно, вместе с поведением движка.

public class SelfCheckTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ea_selfchk_" + Guid.NewGuid().ToString("N"));

    public SelfCheckTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static SelfCheckBaseline MakeBaseline() => new(
        FilesTotal: 5, FilesWithMentions: 4, TotalMentions: 12,
        new Dictionary<string, int> { ["Наг и Йеб"] = 10, ["Азатот"] = 2 });

    private static SelfCheckSnapshot MakeActual(
        int files = 5, int withMentions = 4, int total = 12,
        Dictionary<string, int>? counts = null) => new(
        files, withMentions, total,
        counts ?? new Dictionary<string, int> { ["Наг и Йеб"] = 10, ["Азатот"] = 2 });

    // ─────────────── Verify: чистая функция сверки ───────────────

    [Fact]
    public void Verify_matching_baseline_passes()
    {
        Assert.Empty(SelfCheck.Verify(MakeBaseline(), MakeActual()));
    }

    [Fact]
    public void Verify_reports_totals_mismatch()
    {
        var problems = SelfCheck.Verify(MakeBaseline(), MakeActual(total: 11));

        Assert.Contains(problems, p => p.Contains("Итого упоминаний") && p.Contains("12") && p.Contains("11"));
    }

    [Fact]
    public void Verify_reports_file_counters_mismatch()
    {
        var problems = SelfCheck.Verify(MakeBaseline(), MakeActual(files: 6, withMentions: 2));

        Assert.Contains(problems, p => p.Contains("Файлов в корпусе") && p.Contains("5") && p.Contains("6"));
        Assert.Contains(problems, p => p.Contains("Файлов с находками") && p.Contains("4") && p.Contains("2"));
    }

    [Fact]
    public void Verify_reports_missing_extra_and_shifted_entities()
    {
        var actual = MakeActual(counts: new Dictionary<string, int>
        {
            ["Наг и Йеб"] = 9,   // сдвиг счётчика
            ["Шуб-Ниггурат"] = 3, // лишняя сущность
            // «Азатот» исчез
        });

        var problems = SelfCheck.Verify(MakeBaseline(), actual);

        Assert.Contains(problems, p => p.Contains("Наг и Йеб") && p.Contains("ожидалось 10, получено 9"));
        Assert.Contains(problems, p => p.Contains("Азатот") && p.Contains("получено 0"));
        Assert.Contains(problems, p => p.Contains("Лишняя сущность") && p.Contains("Шуб-Ниггурат"));
    }

    // ─────────────── RunEngine: детерминизм ───────────────

    [Fact]
    public void RunEngine_is_deterministic_across_directories()
    {
        var s1 = SelfCheck.RunEngine(Path.Combine(_dir, "a"));
        var s2 = SelfCheck.RunEngine(Path.Combine(_dir, "b"));

        // Сравнение в каноническом JSON — том же формате, что и файл эталона:
        // это сильнее ссылочного равенства словарей и проверяет сериализацию.
        Assert.True(s1.TotalMentions > 0, "мини-корпус обязан давать находки");
        Assert.Equal(SelfCheck.SnapshotJson(s1), SelfCheck.SnapshotJson(s2));
    }

    // ─────────────── Run: оркестрация ───────────────

    [Fact]
    public void Run_with_matching_explicit_baseline_passes()
    {
        var actual = SelfCheck.RunEngine(Path.Combine(_dir, "probe"));
        var baseline = new SelfCheckBaseline(actual.FilesTotal, actual.FilesWithMentions,
            actual.TotalMentions, actual.EntityCounts);

        var result = SelfCheck.Run(Path.Combine(_dir, "run"), baseline);

        Assert.True(result.Passed, string.Join("; ", result.Problems));
        Assert.Empty(result.Problems);
        Assert.True(result.DurationMs >= 0);
    }

    [Fact]
    public void Run_with_wrong_baseline_fails_with_problems()
    {
        var wrong = new SelfCheckBaseline(999, 999, 999,
            new Dictionary<string, int> { ["Никто"] = 999 });

        var result = SelfCheck.Run(Path.Combine(_dir, "run"), wrong);

        Assert.False(result.Passed);
        Assert.NotEmpty(result.Problems);
    }

    // ─────────────── Встроенный эталон ───────────────

    [Fact]
    public void Embedded_baseline_matches_actual_engine_output()
    {
        var result = SelfCheck.Run(Path.Combine(_dir, "embedded"));

        Assert.True(result.Passed,
            "эталон selfcheck_baseline.json разошёлся с фактом движка: " +
            string.Join("; ", result.Problems));
    }
}

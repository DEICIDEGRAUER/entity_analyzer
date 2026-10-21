using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

// ═══════════════ R1-14: Performance-gate ═══════════════
// Пороговая проверка против baseline: замедление стадии больше порога
// (по умолчанию +30%; поднят с +20% из-за шума микробенчмарка) = красная сборка. Чистая логика без часов —
// детерминирована и проверяема; Bench лишь кормит её замерами.

public class PerformanceGateTests
{
    private static PerfSample S(string name, double ms) => new(name, ms);

    [Fact]
    public void Within_threshold_is_green()
    {
        var baseline = new[] { S("automaton scan", 100.0), S("normalize", 50.0) };
        var current = new[] { S("automaton scan", 119.9), S("normalize", 40.0) };

        Assert.Empty(PerformanceGate.Compare(baseline, current));
    }

    [Fact]
    public void Slower_than_threshold_is_regression()
    {
        var baseline = new[] { S("automaton scan", 100.0) };
        var current = new[] { S("automaton scan", 155.0) }; // +55% > +50% (дефолт ×1.50)

        var regressions = PerformanceGate.Compare(baseline, current);
        var r = Assert.Single(regressions);
        Assert.Equal("automaton scan", r.Name);
        Assert.Equal(100.0, r.BaselineMs);
        Assert.Equal(155.0, r.CurrentMs);
        Assert.Equal(1.55, r.Ratio, precision: 2);
    }

    [Fact]
    public void Noise_band_below_default_threshold_is_green()
    {
        // R1-14: порог поднят до ×1.3 осознанно — шум микробенчмарка
        // (медиана decode utf8 0.34–0.46 мс при эталоне 0.37) давал
        // ложные срабатывания на ×1.25 при неизменном коде.
        var baseline = new[] { S("decode utf8", 100.0) };
        var current = new[] { S("decode utf8", 125.0) };
        Assert.Empty(PerformanceGate.Compare(baseline, current));
    }

    [Fact]
    public void Exactly_at_threshold_is_not_a_regression()
    {
        // Строгое неравенство: ровно +30% — на грани, но зелёное.
        var baseline = new[] { S("stage", 100.0) };
        var current = new[] { S("stage", 130.0) };
        Assert.Empty(PerformanceGate.Compare(baseline, current));
    }

    [Fact]
    public void Faster_stages_are_welcome()
    {
        var baseline = new[] { S("stage", 100.0) };
        var current = new[] { S("stage", 10.0) };
        Assert.Empty(PerformanceGate.Compare(baseline, current));
    }

    [Fact]
    public void Stages_missing_in_current_are_ignored()
    {
        var baseline = new[] { S("gone", 100.0), S("kept", 100.0) };
        var current = new[] { S("kept", 300.0) };

        var regressions = PerformanceGate.Compare(baseline, current);
        Assert.Single(regressions);
        Assert.Equal("kept", regressions[0].Name);
    }

    [Fact]
    public void New_stages_do_not_count()
    {
        var baseline = new[] { S("old", 100.0) };
        var current = new[] { S("old", 100.0), S("brand-new", 999.0) };
        Assert.Empty(PerformanceGate.Compare(baseline, current));
    }

    [Fact]
    public void Custom_threshold_is_honored()
    {
        var baseline = new[] { S("stage", 100.0) };
        var current = new[] { S("stage", 105.0) };

        Assert.Empty(PerformanceGate.Compare(baseline, current, threshold: 1.10));
        Assert.Single(PerformanceGate.Compare(baseline, current, threshold: 1.02));
    }

    [Fact]
    public void Zero_baseline_stage_never_regresses()
    {
        // Защита от деления на ноль: база 0 мс — стадия не оценивается.
        var baseline = new[] { S("instant", 0.0) };
        var current = new[] { S("instant", 5.0) };
        Assert.Empty(PerformanceGate.Compare(baseline, current));
    }
}

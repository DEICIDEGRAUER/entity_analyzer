using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Analysis;

// ═══════════════ R2-13 «Вердикты пользователя» ═══════════════
// Режим «ПОКАЗ» на странице результатов: кроме непроверенных цитат нужны
// выборки уже подтверждённых, ложных и спорных находок.

public class VerdictFilterTests
{
    [Fact]
    public void Verdict_modes_select_expected_rows()
    {
        (VerdictKind? Verdict, string Mode, bool Expected)[] cases =
        [
            (null, VerdictFilter.All, true),
            (VerdictKind.Confirmed, VerdictFilter.All, true),
            (VerdictKind.Rejected, VerdictFilter.All, true),
            (VerdictKind.Uncertain, VerdictFilter.All, true),

            (null, VerdictFilter.Unreviewed, true),
            (VerdictKind.Confirmed, VerdictFilter.Unreviewed, false),
            (VerdictKind.Rejected, VerdictFilter.Unreviewed, false),
            (VerdictKind.Uncertain, VerdictFilter.Unreviewed, false),

            (null, VerdictFilter.Confirmed, false),
            (VerdictKind.Confirmed, VerdictFilter.Confirmed, true),
            (VerdictKind.Rejected, VerdictFilter.Confirmed, false),
            (VerdictKind.Uncertain, VerdictFilter.Confirmed, false),

            (null, VerdictFilter.Rejected, false),
            (VerdictKind.Confirmed, VerdictFilter.Rejected, false),
            (VerdictKind.Rejected, VerdictFilter.Rejected, true),
            (VerdictKind.Uncertain, VerdictFilter.Rejected, false),

            (null, VerdictFilter.Uncertain, false),
            (VerdictKind.Confirmed, VerdictFilter.Uncertain, false),
            (VerdictKind.Rejected, VerdictFilter.Uncertain, false),
            (VerdictKind.Uncertain, VerdictFilter.Uncertain, true),
        ];

        foreach (var (verdict, mode, expected) in cases)
        {
            var actual = VerdictFilter.Matches(verdict, mode);
            Assert.True(expected == actual,
                $"{mode} / {verdict?.ToString() ?? "null"}: expected {expected}, got {actual}");
        }
    }

    [Fact]
    public void Non_verdict_modes_pass_every_row()
    {
        foreach (var mode in new[] { "Точные", "Fuzzy", "Тема…", "Неизвестный режим" })
        {
            Assert.True(VerdictFilter.Matches(null, mode));
            Assert.True(VerdictFilter.Matches(VerdictKind.Confirmed, mode));
            Assert.True(VerdictFilter.Matches(VerdictKind.Rejected, mode));
            Assert.True(VerdictFilter.Matches(VerdictKind.Uncertain, mode));
        }
    }

    [Fact]
    public void Only_verdict_modes_are_reported_as_verdict_modes()
    {
        Assert.True(VerdictFilter.IsVerdictMode(VerdictFilter.Unreviewed));
        Assert.True(VerdictFilter.IsVerdictMode(VerdictFilter.Confirmed));
        Assert.True(VerdictFilter.IsVerdictMode(VerdictFilter.Rejected));
        Assert.True(VerdictFilter.IsVerdictMode(VerdictFilter.Uncertain));

        Assert.False(VerdictFilter.IsVerdictMode(VerdictFilter.All));
        Assert.False(VerdictFilter.IsVerdictMode("Точные"));
        Assert.False(VerdictFilter.IsVerdictMode("Fuzzy"));
        Assert.False(VerdictFilter.IsVerdictMode("Тема…"));
        Assert.False(VerdictFilter.IsVerdictMode(""));
    }
}

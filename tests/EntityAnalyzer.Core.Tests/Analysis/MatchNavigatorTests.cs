using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Analysis;

// ═══════════════ R1-18 «Режим вычитки»: навигация по находкам ═══════════════
// Циклические переходы относительно позиции в тексте (не индекса): окно файла
// передаёт начало текущего подсвеченного прогона (-1 = ничего не выбрано).
// Диапазоны отсортированы и не пересекаются — как после построения подсветки.

public class MatchNavigatorTests
{
    private static readonly MatchRange[] Three =
    [new(10, 13), new(40, 43), new(70, 73)];

    [Fact]
    public void Empty_ranges_return_minus_one()
    {
        Assert.Equal(-1, MatchNavigator.Next([], 0));
        Assert.Equal(-1, MatchNavigator.Prev([], 0));
    }

    [Fact]
    public void Single_match_stays()
    {
        var one = new[] { new MatchRange(5, 8) };
        Assert.Equal(0, MatchNavigator.Next(one, 5));
        Assert.Equal(0, MatchNavigator.Prev(one, 5));
    }

    [Fact]
    public void Nothing_selected_next_starts_first_prev_starts_last()
    {
        Assert.Equal(0, MatchNavigator.Next(Three, -1));
        Assert.Equal(2, MatchNavigator.Prev(Three, -1));
    }

    [Fact]
    public void Next_steps_forward_then_wraps_to_first()
    {
        Assert.Equal(1, MatchNavigator.Next(Three, 10));
        Assert.Equal(2, MatchNavigator.Next(Three, 40));
        Assert.Equal(0, MatchNavigator.Next(Three, 70)); // цикл
    }

    [Fact]
    public void Prev_steps_backward_then_wraps_to_last()
    {
        Assert.Equal(0, MatchNavigator.Prev(Three, 40));
        Assert.Equal(2, MatchNavigator.Prev(Three, 10)); // цикл
    }

    [Fact]
    public void Position_between_matches_picks_the_right_neighbour()
    {
        // Между первым и вторым: вперёд — второе, назад — первое.
        Assert.Equal(1, MatchNavigator.Next(Three, 25));
        Assert.Equal(0, MatchNavigator.Prev(Three, 25));
    }

    [Fact]
    public void Position_beyond_last_wraps_to_first()
    {
        Assert.Equal(0, MatchNavigator.Next(Three, 999));
    }
}

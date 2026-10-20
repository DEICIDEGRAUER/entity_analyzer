using EntityAnalyzer.Core.Text;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

// ═══════════════ R1-7 «Палитра команд» ═══════════════
// Недавние команды: повторный запуск поднимает ключ наверх, дубликаты
// схлопываются, история не растёт бесконечно и переживает пустые значения.

public class RecentCommandHistoryTests
{
    [Fact]
    public void New_command_goes_first_and_keeps_previous_order()
    {
        var result = RecentCommandHistory.Touch(["page:Кодекс", "page:Отчёты"], "page:Обзор");

        Assert.Equal(["page:Обзор", "page:Кодекс", "page:Отчёты"], result);
    }

    [Fact]
    public void Repeated_command_moves_to_front_without_duplicate()
    {
        var result = RecentCommandHistory.Touch(
            ["page:Кодекс", "page:Отчёты", "page:Обзор"], "page:Отчёты");

        Assert.Equal(["page:Отчёты", "page:Кодекс", "page:Обзор"], result);
    }

    [Fact]
    public void History_is_limited_to_the_newest_entries()
    {
        var result = RecentCommandHistory.Touch(
            ["a", "b", "c"], "d", limit: 3);

        Assert.Equal(["d", "a", "b"], result);
    }

    [Fact]
    public void Blank_entries_and_duplicates_are_removed()
    {
        var result = RecentCommandHistory.Touch(
            [" ", "a", "A", "", "b", "a"], null);

        Assert.Equal(["a", "b"], result);
    }

    [Fact]
    public void Zero_limit_clears_history()
    {
        var result = RecentCommandHistory.Touch(["a", "b"], "c", limit: 0);

        Assert.Empty(result);
    }
}

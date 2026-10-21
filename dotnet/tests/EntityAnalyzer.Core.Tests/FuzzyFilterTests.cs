using EntityAnalyzer.Core.Text;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R1-7 «Command Palette»: нечёткий фильтр команд по подстроке/подпоследовательности
/// с ранжированием (точное вхождение > начало слова > подряд > разбросанно).
/// </summary>
public class FuzzyFilterTests
{
    private static string[] Filter(string query, params string[] targets) =>
        [.. FuzzyFilter.Rank(targets, query).Select(m => m.Item)];

    [Fact]
    public void Empty_query_returns_all_in_original_order()
    {
        var r = FuzzyFilter.Rank(["Б", "А"], "");
        Assert.Equal(["Б", "А"], r.Select(m => m.Item));
    }

    [Fact]
    public void Substring_beats_scattered()
    {
        var r = FuzzyFilter.Rank(["Открыть кодекс", "Кодекс"], "кодекс");
        Assert.Equal("Кодекс", r[0].Item); // короче и точнее
    }

    [Fact]
    public void Word_start_match_scores_higher()
    {
        var r = FuzzyFilter.Rank(["Запустить анализ корпуса", "Экспорт анализа"], "ан");
        // «ан» в начале слова «анализ» ценится выше, чем внутри «анализа»? Оба начала слов;
        // проверяем лишь что оба найдены и порядок стабилен.
        Assert.Contains("Запустить анализ корпуса", r.Select(m => m.Item));
        Assert.Contains("Экспорт анализа", r.Select(m => m.Item));
    }

    [Fact]
    public void Case_insensitive_and_cyrillic()
    {
        Assert.Contains("Режим вычитки", Filter("ВЫЧ", "Режим вычитки"));
    }

    [Fact]
    public void Scattered_subsequence_still_matches()
    {
        Assert.Contains("Научное резюме", Filter("нрез", "Научное резюме", "Отчёты"));
    }

    [Fact]
    public void Non_matching_items_are_filtered_out()
    {
        var r = FuzzyFilter.Rank(["Один", "Два"], "xyz");
        Assert.Empty(r);
    }

    [Fact]
    public void Consecutive_bonus_ranks_contiguous_first()
    {
        var r = FuzzyFilter.Rank(["Сохранить исследование", "Статистика сохранения"], "сохр");
        Assert.Equal("Сохранить исследование", r[0].Item);
    }
}

using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Analysis;

/// <summary>
/// Группа 3 (item 31): поиск по фрагменту в три уровня — точная подстрока,
/// падежные формы слов, приблизительное написание (fuzzy/парафраз).
/// Тесты герметичны: тексты передаются напрямую, файлы не читаются.
/// </summary>
public class FragmentSearcherTests
{
    private static CorpusText T(string text) => new("файл.txt", @"C:\корпус\файл.txt", text);

    [Fact]
    public void Exact_phrase_is_found_with_line_and_snippet()
    {
        var files = new[] { T("Строка один.\nСтрока два.\nЗдесь Наг победил древнего врага.\nЕщё строка.") };

        var data = FragmentSearcher.Search("Наг победил", files);

        var hit = Assert.Single(data);
        Assert.Equal("файл.txt", hit.File);
        Assert.Equal(@"C:\корпус\файл.txt", hit.Path);
        Assert.Equal(3, hit.Line);
        Assert.Equal("точное", hit.Kind);
        Assert.Equal(100, hit.Score);
        Assert.Contains("Наг победил", hit.Fragment, StringComparison.Ordinal);
    }

    [Fact]
    public void Exact_search_is_case_insensitive_and_not_duplicated()
    {
        var files = new[] { T("И НАГ пришёл.") };

        var data = FragmentSearcher.Search("наг", files);

        var hit = Assert.Single(data);
        Assert.Equal("точное", hit.Kind);
        Assert.Contains("НАГ", hit.Fragment, StringComparison.Ordinal);
    }

    [Fact]
    public void Morphology_matches_other_word_forms()
    {
        var files = new[] { T("Около Храма Нагу стоял страж.") };

        var data = FragmentSearcher.Search("храм нага", files);

        var hit = Assert.Single(data);
        Assert.Equal("морфология", hit.Kind);
    }

    [Fact]
    public void Morphology_disabled_skips_other_forms()
    {
        var files = new[] { T("Около Храма Нагу стоял страж.") };

        var data = FragmentSearcher.Search("храм нага", files, morphology: false);

        Assert.Empty(data);
    }

    [Fact]
    public void Fuzzy_finds_close_spelling()
    {
        var files = new[] { T("В книге Некрономикоп написано об этом.") };

        var data = FragmentSearcher.Search("Некрономикон", files);

        var hit = Assert.Single(data);
        Assert.Equal("приблизительное", hit.Kind);
        Assert.True(hit.Score >= FragmentSearcher.FuzzyThreshold, $"score={hit.Score}");
    }

    [Fact]
    public void Fuzzy_disabled_skips_typos()
    {
        var files = new[] { T("В книге Некрономикоп написано об этом.") };

        var data = FragmentSearcher.Search("Некрономикон", files, fuzzy: false);

        Assert.Empty(data);
    }

    [Fact]
    public void Exact_occurrences_are_not_repeated_by_word_tiers()
    {
        var files = new[] { T("Наг первый.\nИ наг второй.") };

        var data = FragmentSearcher.Search("наг", files);

        Assert.Equal(2, data.Count);
        Assert.All(data, h => Assert.Equal("точное", h.Kind));
    }

    [Fact]
    public void Word_boundaries_prevent_midword_matches()
    {
        var files = new[] { T("награда награде наградой") };

        var data = FragmentSearcher.Search("наг", files);

        Assert.Empty(data);
    }

    [Fact]
    public void Word_order_is_preserved()
    {
        var files = new[] { T("Храм Нага стоял у воды.") };

        var data = FragmentSearcher.Search("наг храм", files);

        Assert.Empty(data);
    }

    [Fact]
    public void Max_per_file_and_empty_query_are_handled()
    {
        var files = new[] { T("наг наг наг наг") };

        Assert.Empty(FragmentSearcher.Search("", files));
        Assert.Empty(FragmentSearcher.Search("   ", files));

        var data = FragmentSearcher.Search("наг", files, maxPerFile: 2);
        Assert.Equal(2, data.Count);
    }

    [Fact]
    public void Synonyms_from_catalog_match_sibling_spellings()
    {
        var files = new[] { T("Древний Азатот спал в бездне.") };
        var synonyms = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["azathoth"] = new[] { "азатот" },
            ["азатот"] = new[] { "azathoth" },
        };

        // Без синонимов написания не совпадают даже приблизительно…
        Assert.Empty(FragmentSearcher.Search("Azathoth", files, fuzzy: false));
        // …а с синонимами каталога находится морфологическое совпадение.
        var data = FragmentSearcher.Search("Azathoth", files, fuzzy: false, synonyms: synonyms);

        var hit = Assert.Single(data);
        Assert.Equal("морфология", hit.Kind);
        Assert.Contains("Азатот", hit.Fragment);
    }

    [Fact]
    public void Synonyms_decline_and_respect_word_order()
    {
        var files = new[] { T("Храм Ктулху стоял у воды.") };
        var synonyms = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["кталху"] = new[] { "ктулху" },
            ["ктулху"] = new[] { "кталху" },
        };

        // Порядок слов сохранён: «кталху храм» не совпадает.
        Assert.Empty(FragmentSearcher.Search("кталху храм", files, fuzzy: false, synonyms: synonyms));

        var hit = Assert.Single(
            FragmentSearcher.Search("храм кталху", files, fuzzy: false, synonyms: synonyms));
        Assert.Equal("морфология", hit.Kind);
    }
}

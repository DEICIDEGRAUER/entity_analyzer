using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Semantics;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>Тесты расширенных возможностей паттернов: any_of, required, total_window, not.</summary>
public class SemanticPatternsAdvancedTests
{
    private static LexiconSet MakeLexicons()
    {
        var lex = new LexiconSet();
        lex.Add("epithets", ["древний", "запретный"]);
        lex.Add("abstract", ["страх", "безумие"]);
        lex.Add("books", ["фолиант", "том"]);
        lex.Add("negations", ["не", "ни"]);
        return lex;
    }

    private static List<(string Text, int Offset)> Words(string sentence)
    {
        var list = new List<(string, int)>();
        foreach (System.Text.RegularExpressions.Match m
                 in System.Text.RegularExpressions.Regex.Matches(sentence, @"[\p{L}\p{N}]+"))
            list.Add((m.Value, m.Index));
        return list;
    }

    // ─── 1. AnyOf: альтернативы ───

    [Fact]
    public void AnyOf_MatchesEitherOption()
    {
        var p = new SemanticPattern("t", "T", "",
        [
            SemanticToken.Lex("books"),
            SemanticToken.AnyOf(SemanticToken.Lex("epithets"), SemanticToken.Lex("abstract")),
        ], Window: 2);

        var m = new SemanticMatcher([p], MakeLexicons(), []);
        Assert.True(m.TryMatchSequence(p, Words("фолиант древний"), out _, out _));
        Assert.True(m.TryMatchSequence(p, Words("фолиант страх"), out _, out _));
        Assert.False(m.TryMatchSequence(p, Words("фолиант красный"), out _, out _));
    }

    [Fact]
    public void AnyOf_JsonRoundTrip()
    {
        var p = new SemanticPattern("t", "T", "",
        [
            SemanticToken.AnyOf(SemanticToken.Lex("epithets"), SemanticToken.Rx("^\\d$")),
        ]);
        var back = SemanticPatternJson.Parse(SemanticPatternJson.Serialize([p])).Single();
        var tok = back.Tokens[0];
        Assert.Equal(TokenKind.AnyOf, tok.Kind);
        Assert.Equal(2, tok.Options!.Count);
        Assert.Equal(TokenKind.Regex, tok.Options[1].Kind);
    }

    // ─── 2. Optional tokens (required: false) ───

    [Fact]
    public void OptionalToken_MatchesWithAndWithout()
    {
        var p = new SemanticPattern("t", "T", "",
        [
            new SemanticToken(TokenKind.Lexicon, "epithets", Required: false),
            SemanticToken.Lex("books"),
        ], Window: 3);

        var m = new SemanticMatcher([p], MakeLexicons(), []);
        Assert.True(m.TryMatchSequence(p, Words("древний фолиант"), out _, out _));
        Assert.True(m.TryMatchSequence(p, Words("просто фолиант"), out _, out _));
        Assert.False(m.TryMatchSequence(p, Words("вообще ничего"), out _, out _));
    }

    [Fact]
    public void OptionalToken_JsonRoundTrip()
    {
        var p = new SemanticPattern("t", "T", "",
        [
            new SemanticToken(TokenKind.Lexicon, "epithets", Required: false),
            SemanticToken.Lex("books"),
        ]);
        var json = SemanticPatternJson.Serialize([p]);
        Assert.Contains("\"required\": false", json);
        var back = SemanticPatternJson.Parse(json).Single();
        Assert.False(back.Tokens[0].Required);
        Assert.True(back.Tokens[1].Required);
    }

    // ─── 3. TotalWindow: общий размах паттерна ───

    [Fact]
    public void TotalWindow_RejectsWideSpan()
    {
        // Разрывы между соседними токенами малы (≤2), но общий размах велик.
        var p = new SemanticPattern("t", "T", "",
        [
            SemanticToken.Lit("в"),
            SemanticToken.Lit("год"),
            SemanticToken.Lex("books"),
        ], Window: 2, TotalWindow: 4);

        var m = new SemanticMatcher([p], MakeLexicons(), []);

        // «в … год … фолиант»: слова 0→2→4, размах 4 ≤ 4 — проходит.
        Assert.True(m.TryMatchSequence(p, Words("в год том фолиант"), out _, out _));

        // Размах 6 > 4 — отвергается, хотя каждый соседний зазор ≤ 2.
        Assert.False(m.TryMatchSequence(p, Words("в самом деле год и вот фолиант"), out _, out _));
    }

    // ─── 4. Not: негативный страж перед следующим токеном ───

    [Fact]
    public void NotGuard_RejectsNegatedBook()
    {
        var p = new SemanticPattern("t", "T", "",
        [
            SemanticToken.NotBefore(SemanticToken.Lex("negations")),
            SemanticToken.Lex("books"),
        ], Window: 2);

        var m = new SemanticMatcher([p], MakeLexicons(), []);

        // «не фолиант» — страж видит «не» перед «фолиант» → мимо.
        Assert.False(m.TryMatchSequence(p, Words("это не фолиант"), out _, out _));

        // «старый фолиант» — отрицания нет → совпадение.
        Assert.True(m.TryMatchSequence(p, Words("это старый фолиант"), out _, out _));
    }

    [Fact]
    public void NotGuard_CombinesWithOptionalEpithet()
    {
        // Эпитет опционален; страж проверяет слово непосредственно перед книгой.
        var p = new SemanticPattern("t", "T", "",
        [
            new SemanticToken(TokenKind.Lexicon, "epithets", Required: false),
            SemanticToken.NotBefore(SemanticToken.Lex("negations")),
            SemanticToken.Lex("books"),
        ], Window: 3);

        var m = new SemanticMatcher([p], MakeLexicons(), []);
        Assert.True(m.TryMatchSequence(p, Words("запретный фолиант"), out _, out _));
        Assert.False(m.TryMatchSequence(p, Words("не фолиант"), out _, out _));
    }

    [Fact]
    public void NotAtEnd_IsRejectedAsInvalid()
    {
        var p = new SemanticPattern("t", "T", "",
        [
            SemanticToken.Lex("books"),
            SemanticToken.NotBefore(SemanticToken.Lex("negations")),
        ]);
        var m = new SemanticMatcher([p], MakeLexicons(), []);
        Assert.False(m.HasPatterns); // некорректный паттерн отброшен при загрузке
    }

    // ─── 5. Статистика по паттернам в QualityReport ───

    [Fact]
    public void QualityReport_CarriesSemanticStats()
    {
        var q = new QualityReport(
            10, 1, 0, 0, 0, 0, 9, 0, 0, 0,
            [], "1.0s",
            SemanticStats:
            [
                new SemanticPatternStat("forbidden-book", "Запретные книги", 12, 3),
                new SemanticPatternStat("ritual-formula", "Ритуальные формулы", 45, 12),
            ]);

        Assert.Equal(2, q.SemanticStats.Count);
        Assert.Equal(12, q.SemanticStats[0].Matches);
        Assert.Equal(3, q.SemanticStats[0].UniqueFiles);
    }
}

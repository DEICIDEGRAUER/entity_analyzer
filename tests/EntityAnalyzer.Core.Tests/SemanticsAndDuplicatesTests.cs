using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Semantics;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

public class SemanticsAndDuplicatesTests
{
    // ─── Дубликаты текстов ───

    [Fact]
    public void DuplicateGroups_GroupsCopiesWithOriginal()
    {
        var firstByHash = new Dictionary<string, string>
        {
            ["h1"] = @"C:\a.txt",
            ["h2"] = @"C:\b.txt",
        };
        var dups = new List<(string Hash, string Path)>
        {
            ("h1", @"C:\copy1.txt"),
            ("h1", @"C:\copy2.txt"),
            ("h2", @"C:\copy3.txt"),
        };

        var groups = AnalysisEngine.BuildDuplicateGroups(firstByHash, dups);

        Assert.Equal(2, groups.Count);
        Assert.Equal(2, groups[0].RedundantCount);
        Assert.Equal(@"C:\a.txt", groups[0].Files[0]);
        Assert.Contains(@"C:\copy2.txt", groups[0].Files);
        Assert.Single(groups[1].Files.Skip(1));
    }

    [Fact]
    public void DuplicateGroups_EmptyWhenNoDuplicates()
    {
        var groups = AnalysisEngine.BuildDuplicateGroups(
            new Dictionary<string, string>(), []);
        Assert.Empty(groups);
    }

    // ─── Семантические паттерны ───

    private static SemanticMatcher MakeMatcher(params SemanticPattern[] extra)
    {
        var lexicons = new LexiconSet();
        lexicons.Add("epithets", ["древний", "запретный"]);
        lexicons.Add("ritual", ["обряд", "ритуал"]);
        var patterns = new List<SemanticPattern>
        {
            new("p-lit", "Литерал", "", [SemanticToken.Lit("идол")]),
            new("p-seq", "Последовательность", "",
                [SemanticToken.Lex("epithets"), SemanticToken.Prop(), SemanticToken.Lex("ritual")], Window: 3),
            new("p-year", "Год", "", [SemanticToken.Rx(@"^(18|19|20)\d{2}$")]),
            new("p-off", "Выключен", "", [SemanticToken.Lit("запрещённое")], Enabled: false),
        };
        patterns.AddRange(extra);
        return new SemanticMatcher(patterns, lexicons, ["Наг"]);
    }

    private static int[] LineStartsOf(string text) =>
        AnalysisEngine.BuildLineStarts(text);

    [Fact]
    public void Matcher_FindsLiteralWord()
    {
        var m = MakeMatcher();
        var text = "В храме стоял древний идол.";
        var hits = m.Scan(text, "f.txt", LineStartsOf(text));
        Assert.Contains(hits, h => h.PatternId == "p-lit");
    }

    [Fact]
    public void Matcher_MatchesSequenceWithGaps()
    {
        var m = MakeMatcher();
        // эпитет + имя собственное (с заглавной, не в начале) + ритуальный термин
        var text = "Жрецы проводили древний Наг и его ночной обряд у моря.";
        var hits = m.Scan(text, "f.txt", LineStartsOf(text));
        Assert.Contains(hits, h => h.PatternId == "p-seq");
    }

    [Fact]
    public void Matcher_SequenceFailsWhenGapTooWide()
    {
        var m = MakeMatcher();
        // между «древний» и «обряд» больше 3 слов
        var text = "Древний народ забыл старого бога и всякий обряд давно ушёл в песок.";
        var hits = m.Scan(text, "f.txt", LineStartsOf(text));
        Assert.DoesNotContain(hits, h => h.PatternId == "p-seq");
    }

    [Fact]
    public void Matcher_RegexTokenMatchesYear()
    {
        var m = MakeMatcher();
        var text = "Экспедиция вернулась лишь в 1926 году.";
        var hits = m.Scan(text, "f.txt", LineStartsOf(text));
        Assert.Contains(hits, h => h.PatternId == "p-year");
    }

    [Fact]
    public void Matcher_DisabledPatternIsIgnored()
    {
        var m = MakeMatcher();
        var text = "Он открыл запрещённое писание.";
        var hits = m.Scan(text, "f.txt", LineStartsOf(text));
        Assert.DoesNotContain(hits, h => h.PatternId == "p-off");
    }

    [Fact]
    public void Matcher_LineNumberIsCorrect()
    {
        var m = MakeMatcher();
        var text = "Первая строка без находок.\nВторая строка: идол в нише.\nТретья.";
        var hits = m.Scan(text, "f.txt", LineStartsOf(text));
        var lit = hits.First(h => h.PatternId == "p-lit");
        Assert.Equal(2, lit.Line);
    }

    [Fact]
    public void PatternJson_RoundTrip()
    {
        var patterns = new List<SemanticPattern>
        {
            new("x", "X", "desc", [SemanticToken.Lit("a"), SemanticToken.Rx("^\\d$")], 5, false, "cult"),
        };
        var json = SemanticPatternJson.Serialize(patterns);
        var back = SemanticPatternJson.Parse(json);

        var p = back.Single();
        Assert.Equal("x", p.Id);
        Assert.Equal(TokenKind.Regex, p.Tokens[1].Kind);
        Assert.False(p.Enabled);
        Assert.Equal(5, p.Window);
    }

    // ─── Совместные упоминания ───

    private static AnalysisSession MakeSession() =>
        new(
            Files:
            [
                MakeFile("near.txt", [("Наг", 10), ("Йеб", 12)]),
                MakeFile("far.txt", [("Наг", 3), ("Йеб", 400)]),
                MakeFile("only_a.txt", [("Наг", 7)]),
                MakeFile("none.txt", []),
            ],
            Summary: null!, Quality: null!, PresetName: "test");

    private static FileResult MakeFile(string name, params (string Entity, int Line)[] mentions) =>
        new(name, name,
            [.. mentions.Select((m, i) => new Mention(i + 1, m.Entity, m.Entity, m.Line, "контекст"))],
            mentions.Length, 20, 1000, 0);

    [Fact]
    public void CoOccur_SameFileFindsBothFiles()
    {
        var rows = CoOccurrenceQuery.Run(MakeSession(), "Наг", "Йеб", CoOccurMode.SameFile, 5);
        Assert.Equal(["far.txt", "near.txt"], rows.Select(r => r.FileName).ToArray());
    }

    [Fact]
    public void CoOccur_NearLinesFiltersByGap()
    {
        var rows = CoOccurrenceQuery.Run(MakeSession(), "Наг", "Йеб", CoOccurMode.NearLines, 5);
        var row = Assert.Single(rows);
        Assert.Equal("near.txt", row.FileName);
        Assert.Equal(2, row.MinGapLines);
    }

    [Fact]
    public void CoOccur_FarLinesRequiresDistance()
    {
        var rows = CoOccurrenceQuery.Run(MakeSession(), "Наг", "Йеб", CoOccurMode.FarLines, 50);
        var row = Assert.Single(rows);
        Assert.Equal("far.txt", row.FileName);
    }

    [Fact]
    public void CoOccur_WithoutSecondFindsOnlyA()
    {
        var rows = CoOccurrenceQuery.Run(MakeSession(), "Наг", "Йеб", CoOccurMode.FirstWithoutSecond, 5);
        var row = Assert.Single(rows);
        Assert.Equal("only_a.txt", row.FileName);
    }
}

using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Semantics;
using EntityAnalyzer.Core.Variants;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>Интеграционный тест: движок находит дубликаты и семантические конструкции.
/// Сериальная коллекция: тесты крутят EA_SEMANTIC_USER_DIR (общий env процесса).</summary>
[Collection("LogSequential")]
public class EngineSemanticsIntegrationTests : IDisposable
{
    private readonly string _corpus;
    private readonly string _out;
    private readonly string _userDir;

    public EngineSemanticsIntegrationTests()
    {
        // Изоляция от пользовательских файлов машины: чистый каталог конфигов.
        _userDir = Path.Combine(Path.GetTempPath(), "ea_sem_user_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_userDir);
        Environment.SetEnvironmentVariable("EA_SEMANTIC_USER_DIR", _userDir);

        _corpus = Path.Combine(Path.GetTempPath(), "ea_tests_" + Guid.NewGuid().ToString("N")[..8]);
        _out = _corpus + "_out";
        Directory.CreateDirectory(_corpus);

        Write("original.txt",
            "Жрецы проводили древний Наг и его ночной обряд у моря. Экспедиция вернулась лишь в 1926 году.");
        File.Copy(Path.Combine(_corpus, "original.txt"), Path.Combine(_corpus, "copy_of_original.txt"));
        Write("patterns.txt",
            "Верховный жрец Йеба хранил запретный фолиант. Порождение Нага спало в глубинах.");
    }

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(_corpus, name), content, new System.Text.UTF8Encoding(false));

    [Fact]
    public void Engine_FindsDuplicates_AndSemanticPatterns()
    {
        var options = new AnalyzerOptions
        {
            Corpus = { Dir = _corpus },
            Output = { Dir = _out },
            // Изоляция от машинного состояния: общий относительный ".cache"
            // делится между прогонами тестов (вскрыто R1-11).
            Cache = { Dir = Path.Combine(_out, ".cache") },
        };
        Presets.Apply(Presets.Resolve("extended", options), options.Processing);

        var codex = DefaultCodex.Load();
        var definitions = CodexSearchAdapter.ToDefinitions(codex).ToList();
        var catalog = new EntityCatalog(definitions, generateVariants: true);

        var session = new AnalysisEngine(options, catalog, "extended").Run(CancellationToken.None);

        // Дубликаты: original ≡ copy_of_original.
        var dup = Assert.Single(session.DuplicateGroups);
        Assert.Equal(2, dup.Files.Count);

        // Семантика: паттерны срабатывают в файле БЕЗ известных имён тоже.
        var ids = session.SemanticMatches.Select(m => m.PatternId).ToHashSet();
        Assert.Contains("ritual-formula", ids);
        Assert.Contains("temporal-marker", ids);
        Assert.Contains("title-person", ids);
        Assert.Contains("forbidden-book", ids);
        Assert.Contains("spawn-of", ids);
        Assert.Contains(session.SemanticMatches, m => m.FilePath.EndsWith("patterns.txt"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("EA_SEMANTIC_USER_DIR", null);
        try { Directory.Delete(_corpus, recursive: true); } catch { }
        try { Directory.Delete(_out, recursive: true); } catch { }
        try { Directory.Delete(_userDir, recursive: true); } catch { }
    }
}

[Collection("LogSequential")]
public class SemanticPatternStoreTests : IDisposable
{
    private readonly string _userDir;

    public SemanticPatternStoreTests()
    {
        // Изоляция от пользовательского конфига на машине: только встроенные.
        _userDir = Path.Combine(Path.GetTempPath(), "ea_store_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_userDir);
        Environment.SetEnvironmentVariable("EA_SEMANTIC_USER_DIR", _userDir);
    }

    public void Dispose()
    {
        // Item 46: тесты словарей пишут semantic_lexicons.json в общий каталог
        // фикстуры LogSequential — убираем за собой, иначе порядок тестов
        // влияет друг на друга (файл переживает отдельные тесты).
        try { System.IO.File.Delete(SemanticPatternStore.UserLexiconsPath); } catch { }
        Environment.SetEnvironmentVariable("EA_SEMANTIC_USER_DIR", null);
        try { Directory.Delete(_userDir, recursive: true); } catch { }
    }

    [Fact]
    public void Store_LoadsEmbeddedPatternsAndLexicons()
    {
        var store = SemanticPatternStore.Load();
        Assert.Contains(store.Patterns, p => p.Id == "spawn-of" && p.Enabled);
        Assert.Contains(store.Patterns, p => p.Id == "forbidden-book");
        Assert.Contains(store.Patterns, p => p.Id == "temporal-title");
        Assert.True(store.Lexicons.Contains("epithets", "запретный"));
        Assert.True(store.Lexicons.Contains("books", "фолиант"));
        Assert.True(store.Lexicons.Contains("titles", "жрец"));
    }

    /// <summary>Item 46: «!слово» в пользовательских словарях удаляет слово из встроенного.</summary>
    [Fact]
    public void Merge_RemovesWords_WithBangSentinel()
    {
        var baseSet = new LexiconSet();
        baseSet.Add("epithets", ["древний", "запретный"]);
        var overrides = new LexiconSet();
        overrides.Add("epithets", ["!запретный", "студёный"]);

        var merged = LexiconSet.Merge(baseSet, overrides);

        Assert.True(merged.Contains("epithets", "древний"));
        Assert.False(merged.Contains("epithets", "запретный"));
        Assert.True(merged.Contains("epithets", "студёный"));
    }

    /// <summary>Item 46/48: пользовательский файл словарей переживает round-trip, удаление действует.</summary>
    [Fact]
    public void Store_UserLexiconEdits_RoundTrip_AndApply()
    {
        SemanticPatternStore.SaveUserLexiconEdits(new Dictionary<string, List<string>>
        {
            ["epithets"] = ["!запретный", "студёный"],
        });

        var reloaded = SemanticPatternStore.LoadUserLexiconEdits();
        Assert.True(reloaded.TryGetValue("epithets", out var edits));
        Assert.Contains("!запретный", edits);

        var store = SemanticPatternStore.Load();
        Assert.False(store.Lexicons.Contains("epithets", "запретный"));
        Assert.True(store.Lexicons.Contains("epithets", "студёный"));
        Assert.True(store.Lexicons.Contains("epithets", "древний")); // остальные встроенные слова на месте
    }

    /// <summary>Item 46: удаление через «!слово» не ломает битый пользовательский файл.</summary>
    [Fact]
    public void Store_BrokenUserLexiconsFile_FallsBackToEmbedded()
    {
        var path = System.IO.Path.Combine(_userDir, "semantic_lexicons.json");
        System.IO.File.WriteAllText(path, "{ это не json");
        var store = SemanticPatternStore.Load();
        Assert.True(store.Lexicons.Contains("epithets", "запретный"));
    }
}

public class SemanticMatcherDetailedTests
{
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

    [Fact]
    public void FindsLiteralWord()
    {
        var m = MakeMatcher();
        var text = "В храме стоял древний идол.";
        Assert.Contains(m.Scan(text, "f.txt", AnalysisEngine.BuildLineStarts(text)),
            h => h.PatternId == "p-lit");
    }

    [Fact]
    public void MatchesSequenceWithGaps()
    {
        var m = MakeMatcher();
        var text = "Жрецы проводили древний Наг и его ночной обряд у моря.";
        Assert.Contains(m.Scan(text, "f.txt", AnalysisEngine.BuildLineStarts(text)),
            h => h.PatternId == "p-seq");
    }

    [Fact]
    public void SequenceFailsWhenGapTooWide()
    {
        var m = MakeMatcher();
        var text = "Древний народ забыл старого бога и всякий обряд давно ушёл в песок.";
        Assert.DoesNotContain(m.Scan(text, "f.txt", AnalysisEngine.BuildLineStarts(text)),
            h => h.PatternId == "p-seq");
    }

    [Fact]
    public void RegexTokenMatchesYear()
    {
        var m = MakeMatcher();
        var text = "Экспедиция вернулась лишь в 1926 году.";
        Assert.Contains(m.Scan(text, "f.txt", AnalysisEngine.BuildLineStarts(text)),
            h => h.PatternId == "p-year");
    }

    [Fact]
    public void DisabledPatternIsIgnored()
    {
        var m = MakeMatcher();
        var text = "Он открыл запрещённое писание.";
        Assert.DoesNotContain(m.Scan(text, "f.txt", AnalysisEngine.BuildLineStarts(text)),
            h => h.PatternId == "p-off");
    }

    [Fact]
    public void LineNumberIsCorrect()
    {
        var m = MakeMatcher();
        var text = "Первая строка без находок.\nВторая строка: идол в нише.\nТретья.";
        var hit = m.Scan(text, "f.txt", AnalysisEngine.BuildLineStarts(text))
                   .First(h => h.PatternId == "p-lit");
        Assert.Equal(2, hit.Line);
    }

    [Fact]
    public void PatternJson_RoundTrip()
    {
        var patterns = new List<SemanticPattern>
        {
            new("x", "X", "desc", [SemanticToken.Lit("a"), SemanticToken.Rx("^\\d$")], 5, false, "cult"),
        };
        var back = SemanticPatternJson.Parse(SemanticPatternJson.Serialize(patterns));

        var p = back.Single();
        Assert.Equal("x", p.Id);
        Assert.Equal(TokenKind.Regex, p.Tokens[1].Kind);
        Assert.False(p.Enabled);
        Assert.Equal(5, p.Window);
    }
}

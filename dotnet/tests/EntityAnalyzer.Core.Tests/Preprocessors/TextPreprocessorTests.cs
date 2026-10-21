using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Text;
using EntityAnalyzer.Core.Variants;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Preprocessors;

// ═══════════════ R1-13 «Скриптовые препроцессоры» (MVP) ═══════════════
// Контракт ITextPreprocessor + встроенные препроцессоры колонтитулов.
// Пользовательские C#-скрипты (Roslyn) отложены до согласования NuGet;
// реестр имён готов принять их без изменения формата конфигурации.

// LogSequential: Signature_changes_only_with_preprocessors строит подписью
// кэша, которая читает пользовательские semantic-файлы (SemanticPatternStore).
// Параллельно AnalysisModuleTests и фикстура переключают процесс-глобальные
// EA_SEMANTIC_USER_DIR/APPDATA — подпись «прыгала» между вызовами (флейк 1/10).
[Collection("LogSequential")]
public class TextPreprocessorTests
{
    // ─── PageNumberStripper ───

    [Theory]
    [InlineData("12")]
    [InlineData("— 12 —")]
    [InlineData("- 7 -")]
    [InlineData("| 5 |")]
    [InlineData("Стр. 42")]
    [InlineData("Страница 34 из 100")]
    public void Page_numbers_lines_are_removed(string line)
    {
        var doc = "Жрец Нага шептал у алтаря Йеба.\n" + line + "\nИ тьма отвечала ему.";
        var result = new PageNumberStripper().Process(doc, "f.txt");
        Assert.DoesNotContain(line, result);
        Assert.Contains("Жрец Нага", result);
        Assert.Contains("тьма отвечала", result);
    }

    [Fact]
    public void Inline_numbers_are_untouched()
    {
        var doc = "Культ Нага процветал у моря 1000 лет.\nГод Красной Луны наступил в 1928 году.";
        var result = new PageNumberStripper().Process(doc, "f.txt");
        Assert.Equal(doc, result);
    }

    [Fact]
    public void Long_lines_with_digits_are_kept()
    {
        // Строка длиннее порога — это не колонтитул, даже если начинается с числа.
        var longLine = "1928. Верховный жрец хранил запретный фолиант в подвале храма, и никто не смел спускаться туда ночью.";
        var result = new PageNumberStripper().Process(longLine, "f.txt");
        Assert.Equal(longLine, result);
    }

    // ─── RepeatedLineCollapser ───

    [Fact]
    public void Three_identical_lines_collapse_to_one()
    {
        var doc = string.Concat(Enumerable.Repeat("МИФЫ КТУЛХУ — ТОМ I\n", 3)) + "Глава первая.";
        var result = new RepeatedLineCollapser().Process(doc, "f.txt");
        Assert.Equal("МИФЫ КТУЛХУ — ТОМ I\nГлава первая.", result);
    }

    [Fact]
    public void Two_identical_lines_are_kept()
    {
        // Двойной повтор бывает осмысленным (рефрен) — не трогаем.
        var doc = "Наг и Йеб.\nНаг и Йеб.\nДалее текст.";
        Assert.Equal(doc, new RepeatedLineCollapser().Process(doc, "f.txt"));
    }

    [Fact]
    public void Different_lines_are_untouched()
    {
        var doc = "Первая строка.\nВторая строка.\nТретья строка.";
        Assert.Equal(doc, new RepeatedLineCollapser().Process(doc, "f.txt"));
    }

    // ─── Реестр ───

    [Fact]
    public void Registry_resolves_known_names_in_order()
    {
        var r = TextPreprocessorRegistry.Resolve(["page-numbers", "collapse-repeats"]);
        Assert.True(r.IsOk);
        Assert.Equal(["page-numbers", "collapse-repeats"], r.Preprocessors!.Select(p => p.Name));
    }

    [Fact]
    public void Registry_rejects_unknown_name()
    {
        var r = TextPreprocessorRegistry.Resolve(["page-numbers", "roslyn-magic"]);
        Assert.False(r.IsOk);
        Assert.Contains("roslyn-magic", r.Error!, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Интеграция с движком ───

    private static EntityAnalyzer.Core.Analysis.AnalysisSession RunWith(ProcessingOptions setup)
    {
        var corpus = Path.Combine(Path.GetTempPath(), "ea_pp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(corpus);
        // Колонтитул между абзацами ломает контекст цитаты; препроцессор его убирает.
        File.WriteAllText(Path.Combine(corpus, "t1.txt"),
            "Жрец Нага шептал у алтаря Йеба.\n— 12 —\nИ тьма отвечала ему из глубины подземелья.\n",
            new System.Text.UTF8Encoding(false));

        try
        {
            var options = new AnalyzerOptions
            {
                Corpus = { Dir = corpus },
                Output = { Dir = Path.Combine(corpus, "_out") },
                Cache = { Enabled = false },
                Processing = setup,
            };
            var codex = Codex.DefaultCodex.Load();
            var catalog = new EntityCatalog(Codex.CodexSearchAdapter.ToDefinitions(codex).ToList(), generateVariants: true);
            return new AnalysisEngine(options, catalog, "light").Run(CancellationToken.None);
        }
        finally
        {
            try { Directory.Delete(corpus, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Engine_applies_preprocessor_when_enabled()
    {
        var p = new ProcessingOptions();
        Presets.Apply(Presets.Resolve("light", null!), p);
        p.Preprocessors = ["page-numbers"];
        var session = RunWith(p);

        // Цитата не должна содержать маркер страницы.
        var mentions = session.Files.SelectMany(f => f.Mentions).ToList();
        Assert.NotEmpty(mentions);
        Assert.All(mentions, m => Assert.DoesNotContain("— 12 —", m.Context));
    }

    [Fact]
    public void Signature_changes_only_with_preprocessors()
    {
        var corpus = Path.Combine(Path.GetTempPath(), "ea_pp_sig_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(corpus);
        try
        {
            AnalysisEngine Make(List<string>? pp)
            {
                var options = new AnalyzerOptions
                {
                    Corpus = { Dir = corpus },
                    Output = { Dir = Path.Combine(corpus, "_out") },
                };
                if (pp is not null) options.Processing.Preprocessors = pp;
                var catalog = new EntityCatalog([new EntityDefinition("X", "Xx", [], [])]);
                return new AnalysisEngine(options, catalog, "extended");
            }

            var baseSig = Make(null).BuildConfigSignatureForTests();
            var sameSig = Make([]).BuildConfigSignatureForTests();      // пустой список = выкл
            var withPp = Make(["page-numbers"]).BuildConfigSignatureForTests();

            Assert.Equal(baseSig, sameSig);   // байт-паритет при выключенной функции
            Assert.NotEqual(baseSig, withPp); // кэш инвалидируется осознанно
        }
        finally
        {
            try { Directory.Delete(corpus, recursive: true); } catch { }
        }
    }
}

using System.Text.Json;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Exporting;
using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Themes;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// Группа 3 (item 52, R3-1): тематические словари с морфологией, сборщик
/// отрывков по теме/сущности и экспорт MD/CSV/JSON с файлом и позицией.
/// </summary>
public class ThemeExcerptTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea-themes-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static AnalysisSession SessionOf(params FileResult[] files) => new(
        Files: files,
        Summary: new AnalysisSummary(
            files.Sum(f => f.Mentions.Count),
            files.Length,
            1,
            files.Sum(f => f.CharCount),
            files.SelectMany(f => f.Mentions)
                .GroupBy(m => m.Entity, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal)),
        Quality: new QualityReport(10, 2, 1, 0, 0, 0, 3, 0, 1, 2, Array.Empty<(string, string)>(), "1.0s"),
        PresetName: "extended");

    private static AnalysisSession Session() => SessionOf(
        new FileResult("a.txt", "corpus/a.txt",
        [
            new Mention(1, "Наг", "Наг", 12, "У самого моря, на берегу, стоял Наг."),
            new Mention(2, "Наг", "Нага", 20, "Он говорил о погоде и урожае."),
        ], TotalFound: 2, ContextSize: 60, CharCount: 2000, DensityPer10k: 25.0),
        new FileResult("b.txt", "corpus/b.txt",
        [
            new Mention(1, "Ктулху", "Ктулху", 5, "Ктулху спал в глубине океана."),
        ], TotalFound: 1, ContextSize: 60, CharCount: 1000, DensityPer10k: 10.0));

    private static ThemeDictionary Geography() => ThemeCatalog.Builtin[0];

    // ─────────────────────────── матчер ───────────────────────────

    [Fact]
    public void Matcher_matches_declined_and_neuter_forms()
    {
        Assert.Contains("море", ThemeMatcher.MatchWords("На берегу моря", ["море"]));
        Assert.Contains("море", ThemeMatcher.MatchWords("Покрыт МОРЯМИ", ["море"])); // регистр не важен
        Assert.Contains("копьё", ThemeMatcher.MatchWords("В копьях и мечах", ["копьё", "меч"]));
        Assert.Contains("меч", ThemeMatcher.MatchWords("В копьях и мечах", ["копьё", "меч"]));
        Assert.Empty(ThemeMatcher.MatchWords("Он говорил о погоде", ["море"]));
    }

    [Fact]
    public void Matcher_uses_long_stems_but_guards_short_ones()
    {
        // Длинное слово матчится по усечённой основе (география → географический).
        Assert.Contains("география", ThemeMatcher.MatchWords("Географический атлас этих земель", ["география"]));
        // Короткие основы префиксом не матчатся — «море» не ловит «мораль»…
        Assert.Empty(ThemeMatcher.MatchWords("Моральный выбор", ["море"]));
        // …а «поле» (4 буквы) — «полевой».
        Assert.Empty(ThemeMatcher.MatchWords("Полевой словарь", ["поле"]));
    }

    [Fact]
    public void Matcher_reports_hit_spans_for_highlighting()
    {
        var hits = ThemeMatcher.FindHits("У моря и на берегу", ["море", "берег"]);
        Assert.Equal(2, hits.Count);
        Assert.Equal(2, hits[0].Start);          // «моря» после «У »
        Assert.Equal(4, hits[0].Length);
        Assert.Equal("море", hits[0].Word);
        Assert.Equal("берег", hits[1].Word);
        Assert.Equal("моря", "У моря и на берегу".Substring(hits[0].Start, hits[0].Length));
    }

    // ─────────────────────────── словари ───────────────────────────

    [Fact]
    public void Catalog_has_five_builtin_themes()
    {
        var names = ThemeCatalog.Builtin.Select(t => t.Name).ToList();
        Assert.Equal(5, ThemeCatalog.Builtin.Count);
        Assert.Contains("География", names);
        Assert.Contains("Архитектура", names);
        Assert.Contains("Культ", names);
        Assert.Contains("Битвы", names);
        Assert.Contains("Пророчества", names);
        Assert.All(ThemeCatalog.Builtin, t => Assert.True(t.Builtin));
    }

    [Fact]
    public void Catalog_merges_user_words_and_adds_user_themes()
    {
        var merged = ThemeCatalog.Merge(new Dictionary<string, List<string>>
        {
            ["География"] = ["фьорд"],
            ["Космология"] = ["эфир", "первоатом"],
        });

        var geo = merged.Single(t => t.Name == "География");
        Assert.Contains("фьорд", geo.Words);
        Assert.Contains("море", geo.Words); // встроенные слова не потеряны

        var cosmo = merged.Single(t => t.Name == "Космология");
        Assert.False(cosmo.Builtin);
        Assert.Contains("эфир", cosmo.Words);
        Assert.Equal(6, merged.Count);
    }

    // ─────────────────────────── сборщик ───────────────────────────

    [Fact]
    public void Collector_finds_excerpts_by_theme_with_words()
    {
        var excerpts = ThemeExcerptBuilder.Collect(Session(), Geography());

        Assert.Equal(2, excerpts.Count);
        Assert.Equal("a.txt", excerpts[0].FileName);
        Assert.Equal(12, excerpts[0].Line);
        Assert.Equal("Наг", excerpts[0].Entity);
        Assert.Equal(["море", "берег"], excerpts[0].Words);
        Assert.Equal("b.txt", excerpts[1].FileName);
        Assert.Equal(["океан"], excerpts[1].Words);
        Assert.Equal(2, ThemeExcerptBuilder.FileCount(excerpts));
    }

    [Fact]
    public void Collector_filters_by_entity()
    {
        var excerpts = ThemeExcerptBuilder.Collect(Session(), Geography(), entity: "Ктулху");

        Assert.Single(excerpts);
        Assert.Equal("b.txt", excerpts[0].FileName);
    }

    [Fact]
    public void Collector_uses_only_the_mention_context_window()
    {
        // «Море» есть в файле, но вне окна первого упоминания: отрывком
        // становится лишь упоминание, в чьём сохранённом контексте (окне)
        // реально видно слово темы.
        var session = SessionOf(
            new FileResult("w.txt", "corpus/w.txt",
            [
                new Mention(1, "Наг", "Наг", 3, "Наг говорил о погоде и урожае."),
                new Mention(2, "Наг", "Наг", 40, "Наг вышел к самому морю на рассвете."),
            ], TotalFound: 2, ContextSize: 60, CharCount: 500, DensityPer10k: 40.0));

        var only = Assert.Single(ThemeExcerptBuilder.Collect(session, Geography()));

        Assert.Equal(40, only.Line);
        Assert.Equal(["море"], only.Words);
    }

    [Fact]
    public void Collector_dedupes_sorts_and_caps()
    {
        // Дубль упоминания (тот же файл/строка/сущность/вариант) — один отрывок.
        var doubled = SessionOf(
            new FileResult("c.txt", "corpus/c.txt",
            [
                new Mention(1, "Наг", "Наг", 3, "Море шумело."),
                new Mention(2, "Наг", "Наг", 3, "Море шумело."),
            ], TotalFound: 2, ContextSize: 60, CharCount: 100, DensityPer10k: 5.0));
        Assert.Single(ThemeExcerptBuilder.Collect(doubled, Geography()));

        var all = ThemeExcerptBuilder.Collect(Session(), Geography());
        Assert.Equal(["a.txt", "b.txt"], all.Select(e => e.FileName));

        var capped = ThemeExcerptBuilder.Collect(Session(), Geography(), limit: 1);
        Assert.Single(capped);
        Assert.Equal("a.txt", capped[0].FileName);
    }

    // ─────────────────────────── хранилище ───────────────────────────

    [Fact]
    public void Store_roundtrips_user_words()
    {
        var store = new ThemeStore(Path.Combine(_dir, "user_themes.json"));

        Assert.True(store.AddWord("География", "фьорд"));
        Assert.False(store.AddWord("География", "Фьорд")); // дубль без учёта регистра
        Assert.True(store.AddWord("География", "лагуна"));
        Assert.True(store.AddWord("Космология", "эфир"));
        Assert.True(File.Exists(Path.Combine(_dir, "user_themes.json")));

        Assert.Equal(["фьорд", "лагуна"], store.Words("География"));
        Assert.True(store.RemoveWord("География", "лагуна"));
        Assert.Equal(["фьорд"], store.Words("География"));

        // Сливается с встроенной темой.
        var merged = ThemeCatalog.Merge(store.Load());
        Assert.Contains("фьорд", merged.Single(t => t.Name == "География").Words);
    }

    [Fact]
    public void Store_corrupt_file_degrades_to_empty()
    {
        var path = Path.Combine(_dir, "user_themes.json");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "не json вовсе");

        var store = new ThemeStore(path);
        Assert.Empty(store.Load());
        Assert.True(store.AddWord("Битвы", "таран")); // дальше пишется поверх
        Assert.Equal(["таран"], store.Words("Битвы"));
    }

    // ─────────────────────────── экспорт ───────────────────────────

    private List<ThemeExcerpt> Sample() =>
    [
        new("a.txt", "corpus/a.txt", "Наг", "Нага", 12, "У моря молчал Наг", ["море"]),
        new("a.txt", "corpus/a.txt", "Наг", "Наг", 44, "Он сказал \"нет\"; море молчало.", ["море"]),
    ];

    [Fact]
    public void Export_markdown_has_file_and_position()
    {
        var path = ThemeExcerptExport.WriteMarkdown(Sample(), "География", "Наг", Path.Combine(_dir, "excerpts.md"));

        var text = File.ReadAllText(path);
        Assert.Contains("# Отрывки: тема «География», сущность «Наг»", text);
        Assert.Contains("Отрывков: 2 · файлов: 1", text);
        Assert.Contains("## a.txt", text);
        Assert.Contains("- строка 12 · Наг (Нага) · слова темы: море", text);
        Assert.Contains("  > У моря молчал Наг", text);
    }

    [Fact]
    public void Export_csv_escapes_fields()
    {
        var path = ThemeExcerptExport.WriteCsv(Sample(), "География", null, Path.Combine(_dir, "excerpts.csv"));

        var lines = File.ReadAllLines(path);
        Assert.StartsWith("theme;entity;file;line;variant;words;context", lines[0]);
        Assert.Contains("\"Наг\";\"a.txt\";12;\"Нага\";\"море\";\"У моря молчал Наг\"", lines[1]);
        Assert.Contains(";\"Он сказал \"\"нет\"\"; море молчало.\"", lines[2]);
    }

    [Fact]
    public void Export_json_is_parseable()
    {
        var path = ThemeExcerptExport.WriteJson(Sample(), "География", "Наг", Path.Combine(_dir, "excerpts.json"));

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal("География", doc.RootElement.GetProperty("theme").GetString());
        Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
        var first = doc.RootElement.GetProperty("excerpts")[0];
        Assert.Equal("a.txt", first.GetProperty("file").GetString());
        Assert.Equal(12, first.GetProperty("line").GetInt32());
        Assert.Equal("море", first.GetProperty("words")[0].GetString());
    }
}

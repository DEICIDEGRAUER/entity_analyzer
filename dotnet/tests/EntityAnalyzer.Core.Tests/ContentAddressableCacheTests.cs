using System.IO;
using System.Text;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Variants;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R2-5: content-addressable кэш. Ключ кэша результатов — SHA-256 контента
/// («хеш|подпись конфига»), а не путь: переименование/перемещение файла не
/// должно приводить к повторному анализу. Инкрементальный индекс (путь→хеш)
/// служит только экономии чтения; после переименования хеш вычисляется один
/// раз, дальше результат берётся из кэша по контенту.
/// </summary>
[Collection("LogSequential")]
public class ContentAddressableCacheTests : IDisposable
{
    private readonly string _corpus;
    private readonly string _out;

    public ContentAddressableCacheTests()
    {
        _corpus = Path.Combine(Path.GetTempPath(), "ea_cac_" + Guid.NewGuid().ToString("N"));
        _out = Path.Combine(_corpus, "_out");
        Directory.CreateDirectory(_corpus);
    }

    public void Dispose()
    {
        try { Directory.Delete(_corpus, recursive: true); } catch { }
    }

    private AnalysisEngine BuildEngine()
    {
        var options = new AnalyzerOptions
        {
            Corpus = { Dir = _corpus },
            Output = { Dir = _out },
            // Герметичность: относительный Cache.Dir резолвится в CWD процесса
            // и делит кэш между прогонами — задаём абсолютный путь в temp.
            Cache = { Dir = Path.Combine(_out, ".cache") },
        };
        Presets.Apply(Presets.Resolve("light", options), options.Processing);
        var codex = DefaultCodex.Load();
        var definitions = CodexSearchAdapter.ToDefinitions(codex).ToList();
        var catalog = new EntityCatalog(definitions, generateVariants: true);
        return new AnalysisEngine(options, catalog, "light");
    }

    [Fact]
    public void Renamed_file_is_served_from_content_cache_without_reanalysis()
    {
        const string text = "Хроники храма. Наг и Йеб правили веками. Культ Нага процветал.";
        File.WriteAllText(Path.Combine(_corpus, "original.txt"), text, new UTF8Encoding(false));

        // Холодный прогон: анализ в кэш, ни одного попадания.
        var cold = BuildEngine().Run(CancellationToken.None);
        Assert.Equal(0, cold.Quality.FilesUnchangedSkipped);
        Assert.True(cold.Quality.CacheMisses >= 1, "холодный прогон обязан иметь промахи кэша");
        Assert.True(cold.Quality.CacheHits == 0, "холодный прогон не должен давать попаданий");

        // Переименование: путь сменился, байты те же.
        File.Move(Path.Combine(_corpus, "original.txt"), Path.Combine(_corpus, "renamed.txt"));

        // Повторный прогон: файл узнаётся по контенту и берётся из кэша.
        var warm = BuildEngine().Run(CancellationToken.None);
        Assert.Equal(1, warm.Quality.FilesUnchangedSkipped);

        // Результат идентичен холодному: кэш отдаёт корректные данные.
        Assert.Equal(cold.Summary.TotalMentions, warm.Summary.TotalMentions);
        var coldFile = Assert.Single(cold.Files);
        var warmFile = Assert.Single(warm.Files);
        Assert.Equal("renamed.txt", warmFile.FileName);      // имя файла актуальное…
        Assert.Equal(coldFile.Mentions.Count, warmFile.Mentions.Count); // …находки те же
    }

    [Fact]
    public void Changed_content_is_reanalyzed()
    {
        var path = Path.Combine(_corpus, "a.txt");
        File.WriteAllText(path, "Хроники храма. Наг и Йеб правили веками.", new UTF8Encoding(false));
        BuildEngine().Run(CancellationToken.None);

        // Контент изменился → старая запись кэша не подходит.
        File.WriteAllText(path, "Хроники храма. Наг и Йеб правили веками. Йеб уснул.", new UTF8Encoding(false));
        var second = BuildEngine().Run(CancellationToken.None);

        Assert.Equal(0, second.Quality.FilesUnchangedSkipped);
        Assert.True(second.Quality.CacheMisses >= 1, "изменённый файл обязан быть проанализирован заново");
    }

    [Fact]
    public void Mentions_carry_offsets_for_verdict_keys()
    {
        // R2-13: ключ вердикта (file_hash, offset, variant) требует офсета находки.
        // Предложения длинные намеренно: короткие дали бы одинаковый контекст,
        // и дедупликация схлопнула бы обе цитаты в одну (см. README #16).
        File.WriteAllText(Path.Combine(_corpus, "a.txt"),
            "Древние жрецы древнего храма возносили хвалу великому Нагу у подножия молчаливой горы, " +
            "где ночной ветер читал молитвы по рассыпанным камням и остывшему пеплу погасших костров. " +
            "Поздние странники принесли весть о мудром Йебе в цветущий долинный сад, " +
            "где живая вода пела песни о вечном возвращении тёплой весны и зрелых плодах.",
            new UTF8Encoding(false));

        var session = BuildEngine().Run(CancellationToken.None);
        var mentions = session.Files.Single().Mentions;
        Assert.True(mentions.Count >= 2, $"в тестовом файле должно найтись ≥2 упоминания, найдено {mentions.Count}");

        // Каждая находка указывает на свою позицию в тексте.
        Assert.All(mentions, m => Assert.True(m.Offset > 0, $"офсет пуст у [{m.Num}] {m.Variant}"));
        Assert.Equal(mentions.Select(x => x.Offset).Distinct().Count(), mentions.Count);
    }
}

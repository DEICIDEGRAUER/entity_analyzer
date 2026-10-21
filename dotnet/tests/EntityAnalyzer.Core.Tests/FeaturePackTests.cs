using System.IO.Compression;
using System.Text;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Filtering;
using EntityAnalyzer.Core.IO;
using EntityAnalyzer.Core.Logging;
using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Variants;
using Xunit;

// ═══════════════ 15. Структурированный лог ═══════════════

/// <summary>
/// Тестовая инфраструктура чтения свежих файлов. BUG-011: лог-файл в %TEMP%
/// может быть кратковременно захвачен внешним процессом (антивирус/индексатор
/// Windows сканируют только что созданный .log) — прямой File.ReadAllText
/// падал IOException на полном прогоне (флакично, вне зависимости от кода).
/// Читаем с FileShare.ReadWrite|Delete и повторами — тест перестаёт зависеть
/// от машинного состояния.
/// </summary>
internal static class TestFiles
{
    internal static string ReadAllTextWithRetry(string path, int attempts = 10, int delayMs = 150)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs);
                return reader.ReadToEnd();
            }
            catch (IOException) when (attempt < attempts)
            {
                Thread.Sleep(delayMs);
            }
        }
    }
}

[Collection("LogSequential")]
public class LoggingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea_logs_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Log_writes_lines_to_file_with_levels()
    {
        Log.Init(_dir, LogLevel.Debug);
        Log.Info("Cat", "Привет {0}", 42);
        Log.Warn("Cat", "Осторожно");
        Log.Flush();
        var files = Directory.GetFiles(_dir, "app-*.log", SearchOption.AllDirectories);
        Assert.True(files.Length > 0,
            $"нет лог-файлов в {_dir}; существует: [{string.Join(", ", Directory.Exists(_dir) ? Directory.GetFileSystemEntries(_dir) : [])}]");
        var text = TestFiles.ReadAllTextWithRetry(files[0]); // BUG-011: устойчивое чтение
        Assert.Contains("[INFO] [Cat] Привет 42", text);
        Assert.Contains("[WARN] [Cat] Осторожно", text);
    }

    [Fact]
    public void Min_level_filters_debug()
    {
        Log.Init(_dir, LogLevel.Info);
        Log.Debug("Cat", "скрыто");
        Log.Error("Cat", "видно");
        Log.Flush();
        var text = TestFiles.ReadAllTextWithRetry(Directory.GetFiles(_dir, "app-*.log").Single()); // BUG-011
        Assert.DoesNotContain("скрыто", text);
        Assert.Contains("видно", text);
    }

    [Fact]
    public void Ring_buffer_keeps_recent_entries()
    {
        Log.Init(_dir, LogLevel.Debug);
        Log.Info("T", "кольцо");
        Assert.Contains(Log.Recent, e => e.Message.Contains("кольцо"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
}

// ═══════════════ 2. Морфология ═══════════════

public class RussianMorphologyTests
{
    [Fact]
    public void Consonant_stem_declines()
    {
        var forms = RussianMorphology.Decline("Наг").ToList();
        Assert.Contains("Нага", forms);
        Assert.Contains("Нагу", forms);
        Assert.Contains("Нагом", forms);
        Assert.Contains("Наге", forms);
        Assert.Contains("Наги", forms);
        Assert.Contains("Нагов", forms);
    }

    [Fact]
    public void Y_final_declines()
    {
        var forms = RussianMorphology.Decline("Йеб").ToList();
        Assert.Contains("Йеба", forms);
        Assert.Contains("Йебу", forms);
        Assert.Contains("Йебом", forms);
    }

    [Fact]
    public void Hyphenated_name_declines_last_part()
    {
        var forms = RussianMorphology.Decline("Шуб-Ниггурат").ToList();
        Assert.Contains("Шуб-Ниггурата", forms);
        Assert.Contains("Шуб-Ниггурату", forms);
    }

    [Fact]
    public void Latin_names_are_not_declined()
    {
        Assert.Empty(RussianMorphology.Decline("Nug"));
    }

    [Fact]
    public void Factory_absorbs_morphology_for_russian_variants()
    {
        var f = new VariantFactory(600);
        var pool = f.Generate("Йеб", deepCorrupt: false);
        Assert.Contains("Йеба", pool);
        Assert.Contains("Йебу", pool);
    }
}

// ═══════════════ 3. Форматы документов ═══════════════

public class DocumentReaderTests
{
    private static byte[] ZipEntryBytes(string entryName, byte[] content)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var e = zip.CreateEntry(entryName);
            using var es = e.Open();
            es.Write(content, 0, content.Length);
        }
        return ms.ToArray();
    }

    [Fact]
    public void Html_is_stripped_to_text()
    {
        const string html = "<html><head><style>.x{}</style></head><body><h1>Азатот</h1>" +
            "<p>Жрец <b>Нага</b> шептал.&amp;nbsp;&lt;тайна&gt;</p><script>evil()</script></body></html>";
        var text = DocumentReader.Extract("book.html", Encoding.UTF8.GetBytes(html));
        Assert.Contains("Азатот", text);
        Assert.Contains("Жрец Нага шептал.", text);
        Assert.Contains("<тайна>", text);
        Assert.DoesNotContain("evil()", text);
        Assert.DoesNotContain("<b>", text);
    }

    [Fact]
    public void Epub_spine_order_is_respected()
    {
        var opf = """<?xml version="1.0"?><package xmlns="http://www.idpf.org/2007/opf"><manifest><item id="c2" href="ch2.xhtml" media-type="application/xhtml+xml"/><item id="c1" href="ch1.xhtml" media-type="application/xhtml+xml"/></manifest><spine><itemref idref="c1"/><itemref idref="c2"/></spine></package>""";
        var container = """<?xml version="1.0"?><container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container"><rootfiles><rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/></rootfiles></container>""";

        var ms = new MemoryStream(); // expandable — ZipArchive в Update-режиме требует этого
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Put(string name, string content)
            {
                var e = zip.CreateEntry(name);
                using var s = e.Open();
                var b = Encoding.UTF8.GetBytes(content);
                s.Write(b, 0, b.Length);
            }
            Put("mimetype", "application/epub+zip");
            Put("META-INF/container.xml", container);
            Put("OEBPS/content.opf", opf);
            Put("OEBPS/ch1.xhtml", "<html><body><p>Первая Азатот</p></body></html>");
            Put("OEBPS/ch2.xhtml", "<html><body><p>Вторая Йеб</p></body></html>");
        }

        var text = DocumentReader.Extract("book.epub", ms.ToArray());
        Assert.True(text.IndexOf("Первая Азатот") < text.IndexOf("Вторая Йеб"), "spine order broken");
    }

    [Fact]
    public void Docx_paragraphs_become_lines()
    {
        const string doc = """<?xml version="1.0"?><w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Алтарь Азатота</w:t></w:r></w:p><w:p><w:r><w:t>стоял в тьме</w:t></w:r></w:p></w:body></w:document>""";
        var bytes = ZipEntryBytes("word/document.xml", Encoding.UTF8.GetBytes(doc));
        var text = DocumentReader.Extract("doc.docx", bytes);
        Assert.Contains("Алтарь Азатот", text);
        Assert.Contains("стоял в тьме", text);
    }

    [Fact]
    public void Simple_pdf_text_is_extracted()
    {
        // Минимальный несжатый PDF-контент со строковыми операторами Tj/TJ.
        const string content = "BT /F1 12 Tf 72 720 Td (The cult of Azathot) Tj ET\r\nBT [(deep) 120 (Yeb)] TJ ET";
        var pdf = BuildMinimalPdf(content);
        var text = DocumentReader.Extract("doc.pdf", pdf);
        Assert.Contains("The cult of Azathot", text);
        Assert.Contains("deep", text);
        Assert.Contains("Yeb", text);
    }

    private static byte[] BuildMinimalPdf(string contentStream)
    {
        var sb = new StringBuilder();
        sb.Append("%PDF-1.4\n");
        sb.Append("1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n");
        sb.Append("2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n");
        sb.Append("3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]/Contents 4 0 R>>endobj\n");
        sb.Append($"4 0 obj<</Length {contentStream.Length}>>stream\n{contentStream}\nendstream endobj\n");
        sb.Append("trailer<</Root 1 0 R>>\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}

// ═══════════════ 1. Regex-исключения ═══════════════

public class UserExclusionTests
{
    [Fact]
    public void Matching_pattern_rejects_context()
    {
        var f = new UserExclusionFilter(["наград[ау]", "\\bоглавление\\b"]);
        Assert.True(f.IsExcluded("За храбрость воину дали награду от царя."));
        Assert.False(f.IsExcluded("Жрец Азатота читал запретный текст."));
    }

    [Fact]
    public void Invalid_regex_is_skipped_silently()
    {
        var f = new UserExclusionFilter(["([invalid", "нормальный"]);
        Assert.True(f.IsExcluded("тут нормальный случай"));
        Assert.False(f.IsExcluded("любой другой"));
    }

    [Fact]
    public void Options_toggle_and_signature_includes_patterns()
    {
        var p = new ProcessingOptions { UserExcludePatterns = ["награда"] };
        Assert.True(p.UserExclusionsEnabled);
        var engine = new AnalysisEngine(new AnalyzerOptions
        {
            Processing = p,
            Output = new OutputOptions { Dir = Path.Combine(Path.GetTempPath(), "ea_sig_" + Guid.NewGuid().ToString("N")) }
        }, new EntityCatalog([new EntityDefinition("X", "Xx", [], [])]));
        var sig1 = engine.BuildConfigSignatureForTests();
        p.UserExcludePatterns = ["другое"];
        var sig2 = engine.BuildConfigSignatureForTests();
        Assert.NotEqual(sig1, sig2);
    }
}

// ═══════════════ 16. Тайминги стадий ═══════════════

public class StageTimingTests
{
    [Fact]
    public void Stages_are_measured_and_reported()
    {
        var corpus = Path.Combine(Path.GetTempPath(), "ea_stage_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(corpus);
        File.WriteAllText(Path.Combine(corpus, "a.txt"), "Жрец Нага шептал у алтаря Йеба всю ночь напролёт.");

        var options = new AnalyzerOptions
        {
            Corpus = new CorpusOptions { Dir = corpus },
            Output = new OutputOptions { Dir = Path.Combine(corpus, "out") },
            Cache = new CacheOptions { Enabled = false },
        };
        var catalog = new EntityCatalog([new EntityDefinition("Наг и Йеб", "Nug and Yeb", ["Нага"], [])]);
        var session = new AnalysisEngine(options, catalog).Run(CancellationToken.None);

        Assert.True(session.Quality.StageMs.Count >= 5);
        Assert.All(session.Quality.StageMs.Values, v => Assert.True(v >= 0));
        Assert.Contains("read", session.Quality.StageMs.Keys);
        Assert.Contains("match", session.Quality.StageMs.Keys);
    }
}

// ═══════════════ 6. Инкрементальный режим ═══════════════

public class IncrementalIndexTests
{
    [Fact]
    public void Unchanged_file_hash_comes_from_index_without_reading()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ea_inc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "a.txt");
        File.WriteAllText(file, "Текст про Нага и Йеба.");

        var store = new IncrementalIndexStore(Path.Combine(dir, ".idx"));
        var h1 = store.GetOrComputeHash(file, compute: () =>
        {
            var bytes = File.ReadAllBytes(file);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        });
        store.Flush();

        // Второй раз: файл не читаем (compute не вызывается).
        var store2 = new IncrementalIndexStore(Path.Combine(dir, ".idx"));
        var called = false;
        var h2 = store2.GetOrComputeHash(file, compute: () => { called = true; return ""; });
        Assert.Equal(h1, h2);
        Assert.False(called);
    }

    [Fact]
    public void Changed_file_invalidates_entry()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ea_inc2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "a.txt");
        File.WriteAllText(file, "v1");

        var store = new IncrementalIndexStore(Path.Combine(dir, ".idx"));
        store.GetOrComputeHash(file, () => "hash_v1");
        store.Flush();

        Thread.Sleep(20);
        File.WriteAllText(file, "v2 longer content");

        var store2 = new IncrementalIndexStore(Path.Combine(dir, ".idx"));
        var recomputed = false;
        store2.GetOrComputeHash(file, () => { recomputed = true; return "hash_v2"; });
        Assert.True(recomputed);
    }
}

// ═══════════════ 8. Виртуальные корпуса ═══════════════

public class VirtualCorpusTests
{
    [Fact]
    public void Engine_scans_extra_dirs()
    {
        var d1 = Path.Combine(Path.GetTempPath(), "ea_vc1_" + Guid.NewGuid().ToString("N"));
        var d2 = Path.Combine(Path.GetTempPath(), "ea_vc2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(d1); Directory.CreateDirectory(d2);
        File.WriteAllText(Path.Combine(d1, "one.txt"), "Храм Нага стоял веками.");
        File.WriteAllText(Path.Combine(d2, "two.txt"), "Имя Йеба запрещено здесь.");

        var options = new AnalyzerOptions
        {
            Corpus = new CorpusOptions { Dir = d1, ExtraDirs = [d2] },
            Output = new OutputOptions { Dir = Path.Combine(d1, "out") },
            Cache = new CacheOptions { Enabled = false },
        };
        var catalog = new EntityCatalog([new EntityDefinition("Наг и Йеб", "Nug and Yeb", [], [])]);
        var session = new AnalysisEngine(options, catalog).Run(CancellationToken.None);

        Assert.Equal(2, session.Files.Count);
    }
}

// ═══════════════ 4/5. Граф и облако ═══════════════

public class GraphAndCloudTests
{
    private static AnalysisSession MakeSession()
    {
        Mention m(string entity, string variant, string ctx) =>
            new(1, entity, variant, 1, ctx);
        var files = new List<FileResult>
        {
            new("a.txt", "a", [m("Наг и Йеб", "Нага", "Жрец Нага у алтаря"), m("Шуб-Ниггурат", "Шуб-Ниггурат", "коза с тысячей молодых")], 2, 10, 100, 0),
            new("b.txt", "b", [m("Наг и Йеб", "Йеб", "имя Йеба запретили"), m("Азатот", "Азатот", "центр вселенной"), m("Шуб-Ниггурат", "Шуб-Ниггурат", "лес бесплодных холмов")], 3, 10, 100, 0),
        };
        var summary = new AnalysisSummary(5, 2, 3, 200,
            new Dictionary<string, int> { ["Наг и Йеб"] = 2, ["Шуб-Ниггурат"] = 2, ["Азатот"] = 1 });
        return new AnalysisSession(files, summary,
            new QualityReport(10, 1, 0, 0, 0, 0, 5, 0, 0, 0, [], "0.1s"), "test");
    }

    [Fact]
    public void Cooccurrence_counts_shared_files()
    {
        var g = CoOccurrenceGraph.Build(MakeSession());
        Assert.Equal(3, g.Nodes.Count);
        var nagShub = g.Links.First(l =>
            (l.Source == "Наг и Йеб" && l.Target == "Шуб-Ниггурат") ||
            (l.Source == "Шуб-Ниггурат" && l.Target == "Наг и Йеб"));
        Assert.Equal(2, nagShub.Weight); // оба файла содержат обе сущности
    }

    [Fact]
    public void Graph_html_is_self_contained()
    {
        var outDir = Path.Combine(Path.GetTempPath(), "ea_graph_" + Guid.NewGuid().ToString("N"));
        var path = ReportExporter.WriteCoOccurrenceGraph(MakeSession(), outDir);
        var html = File.ReadAllText(path);
        Assert.Contains("<canvas", html);
        Assert.Contains("Наг и Йеб", html);
        Assert.DoesNotContain("src=\"http", html);
    }

    [Fact]
    public void Cloud_html_lists_variants_by_frequency()
    {
        var outDir = Path.Combine(Path.GetTempPath(), "ea_cloud_" + Guid.NewGuid().ToString("N"));
        var path = ReportExporter.WriteVariantCloud(MakeSession(), outDir);
        var html = File.ReadAllText(path);
        Assert.Contains("Нага", html);
        Assert.Contains("Йеб", html);
        Assert.Contains("cloud", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Markdown_report_written()
    {
        var outDir = Path.Combine(Path.GetTempPath(), "ea_md_" + Guid.NewGuid().ToString("N"));
        var path = ReportExporter.WriteMarkdown(MakeSession(), outDir);
        var md = File.ReadAllText(path);
        Assert.StartsWith("# ", md);
        Assert.Contains("| Файл |", md);
        Assert.Contains("> Жрец Нага у алтаря", md);
    }
}

// ═══════════════ 18. Группы сущностей ═══════════════

public class EntityGroupTests
{
    [Fact]
    public void Group_survives_json_roundtrip()
    {
        var doc = CatalogDocument.FromEntities(
        [
            new EntityDefinition("A", "Aa", [], [], Group: "Внешние"),
            new EntityDefinition("B", "Bb", [], []),
        ]);
        var restored = CatalogDocument.FromJson(doc.ToJson());
        Assert.Equal("Внешние", restored.Entities[0].Group);
        Assert.Equal("", restored.Entities[1].Group);
    }

    [Fact]
    public void Summary_counts_mentions_per_group()
    {
        var corpus = Path.Combine(Path.GetTempPath(), "ea_grp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(corpus);
        File.WriteAllText(Path.Combine(corpus, "a.txt"), "Жрец Нага и жрец Йеба встретились у алтаря.");

        var options = new AnalyzerOptions
        {
            Corpus = new CorpusOptions { Dir = corpus },
            Output = new OutputOptions { Dir = Path.Combine(corpus, "out") },
            Cache = new CacheOptions { Enabled = false },
        };
        var catalog = new EntityCatalog([
            new EntityDefinition("Наг и Йеб", "Nug and Yeb", ["Нага", "Йеба"], [], Group: "Великие Древние"),
        ]);
        var session = new AnalysisEngine(options, catalog).Run(CancellationToken.None);
        Assert.True(session.Summary.GroupCounts.TryGetValue("Великие Древние", out var n) && n > 0);
    }
}

// ═══════════════ 13/14. История прогонов и журнал каталога ═══════════════

public class HistoryAndChangeLogTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea_hist_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void History_appends_and_reads_back()
    {
        var store = new HistoryStore(Path.Combine(_dir, "history.jsonl"));
        store.Append(new RunRecord
        {
            StartedAt = DateTimeOffset.Now,
            Preset = "extended",
            TotalMentions = 42,
            FilesWithMentions = 7,
            UniqueEntities = 2,
            DurationSeconds = 3.5,
            RawMatches = 100,
            FinalMentions = 42,
            CorpusDirs = ["C:\\corp"],
        });
        store.Append(new RunRecord { StartedAt = DateTimeOffset.Now, Preset = "light", TotalMentions = 5 });

        var loaded = new HistoryStore(Path.Combine(_dir, "history.jsonl")).ReadLast(10);
        Assert.Equal(2, loaded.Count);
        Assert.Equal("light", loaded[^1].Preset);
        Assert.Equal(42, loaded[0].TotalMentions);
    }

    [Fact]
    public void Catalog_changelog_records_actions()
    {
        var log = new CatalogChangeLog(Path.Combine(_dir, "catalog_history.jsonl"));
        log.Record("toggle", "Наг и Йеб", "enabled=false");
        log.Record("edit", "Азатот", "titles+1");

        var entries = CatalogChangeLog.Read(Path.Combine(_dir, "catalog_history.jsonl"), 10);
        Assert.Equal(2, entries.Count);
        Assert.Equal("toggle", entries[0].Action);
        Assert.Equal("Азатот", entries[^1].Entity);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }
}

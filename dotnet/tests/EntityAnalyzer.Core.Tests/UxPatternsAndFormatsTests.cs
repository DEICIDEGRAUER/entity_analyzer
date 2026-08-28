using System.IO.Compression;
using System.Text;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.IO;
using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Semantics;
using EntityAnalyzer.Core.Variants;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// Тесты пакета UX-улучшений: токен capitalized, повторы min/max,
/// паттерн «Год Красной Луны», новые форматы файлов (rtf/fb2/odt/mobi/cbz),
/// пер-паттернные папки отчётов и обособленный скан паттернов.
/// </summary>
[Collection("LogSequential")]
public class UxPatternsAndFormatsTests
{
    private static LexiconSet Lex() => new();

    private static List<(string Text, int Offset)> Words(string sentence)
    {
        var list = new List<(string, int)>();
        foreach (System.Text.RegularExpressions.Match m
                 in System.Text.RegularExpressions.Regex.Matches(sentence, @"[\p{L}\p{N}]+"))
            list.Add((m.Value, m.Index));
        return list;
    }

    // ─── 1. Capitalized + повторы: «Год Красной Луны» ───

    [Fact]
    public void CapitalizedRepeat_MatchesPeriodTitles()
    {
        var p = new SemanticPattern("temporal-title", "Названия периодов", "",
        [
            SemanticToken.AnyOf(
                SemanticToken.Lit("год"), SemanticToken.Lit("года"),
                SemanticToken.Lit("году"), SemanticToken.Lit("час"),
                SemanticToken.Lit("часа"), SemanticToken.Lit("эра"),
                SemanticToken.Lit("эры"), SemanticToken.Lit("век")),
            SemanticToken.Cap(1, 3),
        ], Window: 2);

        var m = new SemanticMatcher([p], Lex(), []);
        var ok = m.TryMatchSequence(p, Words("В летописи сказано: настал Год Красной Луны."), out _, out _);
        Assert.True(ok);
    }

    [Fact]
    public void CapitalizedRepeat_MatchesHourAndEra()
    {
        var p = new SemanticPattern("t", "T", "",
            [SemanticToken.Lit("час"), SemanticToken.Cap(1, 3)], Window: 2);
        var m = new SemanticMatcher([p], Lex(), []);
        Assert.True(m.TryMatchSequence(p, Words("Наступил Час Воющей Собаки."), out _, out _));

        var p2 = new SemanticPattern("t2", "T2", "",
            [SemanticToken.Lit("эры"), SemanticToken.Cap(1, 3)], Window: 2);
        Assert.True(m.TryMatchSequence(p2, Words("С начала Эры Великой Расы прошло время."), out _, out _));
    }

    [Fact]
    public void Capitalized_DoesNotMatchLowercase()
    {
        var p = new SemanticPattern("t", "T", "",
            [SemanticToken.Lit("год"), SemanticToken.Cap(1, 1)], Window: 2);
        var m = new SemanticMatcher([p], Lex(), []);
        // «год назад» — после «год» слово с маленькой буквы → мимо.
        Assert.False(m.TryMatchSequence(p, Words("Прошёл год назад и всё изменилось."), out _, out _));
    }

    [Fact]
    public void DefaultTemporalTitle_PatternIsLoaded()
    {
        var store = SemanticPatternStore.Load();
        Assert.Contains(store.Patterns, p => p.Id == "temporal-title" && p.Enabled);
    }

    // ─── 2. Повторы: диапазон длины ───

    [Fact]
    public void RepeatMinMax_RespectsBounds()
    {
        var p = new SemanticPattern("t", "T", "",
            [SemanticToken.Any(), SemanticToken.Cap(2, 3)], Window: 0);

        var m = new SemanticMatcher([p], Lex(), []);
        // Ровно 2 капитализированных подряд — ок.
        Assert.True(m.TryMatchSequence(p, Words("видел Красной Луны вчера"), out _, out _));
        // Одно капитализированное — не хватает.
        Assert.False(m.TryMatchSequence(p, Words("видел Луны вчера вечером"), out _, out _));
    }

    // ─── 3. Форматы: RTF ───

    [Fact]
    public void Rtf_ExtractsTextWithUnicode()
    {
        const string rtf = @"{\rtf1\ansi{\fonttbl{\f0 Arial;}}\f0\fs20 " +
                           @"{\*\themedata skip}Год \u8704? Красной \par Луны\par}";
        var text = DocumentReader.Extract("book.rtf", Encoding.UTF8.GetBytes(rtf));
        Assert.Contains("Красной", text);
        Assert.Contains("Луны", text);
        Assert.DoesNotContain("fonttbl", text);   // служебная группа пропущена
        Assert.DoesNotContain("themedata", text); // \* группа пропущена
    }

    // ─── 4. Форматы: FB2 ───

    [Fact]
    public void Fb2_ExtractsBodySkippingNotes()
    {
        const string fb2 =
            """
            <?xml version="1.0"?><FictionBook><body>
            <section><title>Глава 1</title><p>Год Красной Луны настал.</p></section>
            </body><body name="notes"><p>сноска</p></body></FictionBook>
            """;
        var text = DocumentReader.Extract("book.fb2", Encoding.UTF8.GetBytes(fb2));
        Assert.Contains("Год Красной Луны настал.", text);
        Assert.Contains("Глава 1", text);
        Assert.DoesNotContain("сноска", text);
    }

    // ─── 5. Форматы: ODT ───

    [Fact]
    public void Odt_ExtractsContentXml()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("content.xml");
            File.WriteAllText(Path.GetTempFileName(), ""); // no-op для ясности
            using var w = new StreamWriter(entry.Open(), Encoding.UTF8);
            w.Write("""<office:document-content><text:p>Час Воющей Собаки</text:p><text:p>вторая строка</text:p></office:document-content>""");
        }
        ms.Position = 0;
        var text = DocumentReader.Extract("book.odt", ms.ToArray());
        Assert.Contains("Час Воющей Собаки", text);
        Assert.Contains("вторая строка", text);
    }

    // ─── 6. Форматы: MOBI (PalmDOC LZ77) ───

    [Fact]
    public void Mobi_ExtractsPalmDocCompressedText()
    {
        // Готовим текст записи и сжимаем его PalmDOC-ом.
        var compressed = PalmDocCompressForTest(Encoding.ASCII.GetBytes(
            "The Year of the Red Moon was written in Sarnath chronicles."));

        var record0 = BuildRecord0(compression: 2, textRecords: 1, extraFlags: 0);
        var pdb = BuildPdb(record0, [compressed]);

        var text = DocumentReader.Extract("book.mobi", pdb);
        Assert.Contains("Red Moon", text.Replace('\n', ' '));
    }

    /// <summary>Минимальный PalmDOC-компрессор для теста: литералы + простые копии.</summary>
    private static byte[] PalmDocCompressForTest(byte[] raw)
    {
        var outBytes = new List<byte>();
        var literals = new List<byte>();
        foreach (var b in raw)
        {
            if (b is >= 1 and <= 0x7F) literals.Add(b);
            else { FlushLiterals(); outBytes.Add(0); outBytes.Add(b); }
        }
        FlushLiterals();
        return [.. outBytes];

        void FlushLiterals()
        {
            for (var i = 0; i < literals.Count; i += 8)
            {
                var chunk = Math.Min(8, literals.Count - i);
                outBytes.Add((byte)chunk);
                for (var n = 0; n < chunk; n++) outBytes.Add(literals[i + n]);
            }
            literals.Clear();
        }
    }

    private static byte[] BuildRecord0(ushort compression, ushort textRecords, int extraFlags)
    {
        var r = new byte[256]; // реальный record0 ≥ 0xF4 байт (MOBI-заголовок + extra flags)
        WriteU16(r, 0, compression);
        WriteU16(r, 8, textRecords);
        WriteU16(r, 10, 4096);
        WriteU16(r, 12, 0); // без DRM
        // MOBI header
        r[16] = (byte)'M'; r[17] = (byte)'O'; r[18] = (byte)'B'; r[19] = (byte)'I';
        WriteU32(r, 20, 0xE4 - 16); // длина MOBI-заголовка (упрощённо)
        WriteU32(r, 0xF2, (uint)extraFlags);
        return r;
    }

    private static byte[] BuildPdb(byte[] record0, byte[][] records)
    {
        // record0 всегда занимает свои полные 232 байта (MOBI-заголовок читается
        // по смещению 0xF2 от его начала — файл обязан содержать эти байты).
        var count = records.Length + 1;
        var sizes = new List<int> { record0.Length };
        sizes.AddRange(records.Select(r => r.Length));

        var offsets = new List<int> { 78 + 8 * count };
        foreach (var size in sizes) offsets.Add(offsets[^1] + size);

        var total = offsets[^1];
        var pdb = new byte[total];
        pdb[60] = (byte)'B'; pdb[61] = (byte)'O'; pdb[62] = (byte)'K'; pdb[63] = (byte)'M';
        WriteU16(pdb, 76, (ushort)count);

        WriteU32(pdb, 78, (uint)offsets[0]);
        Array.Copy(record0, 0, pdb, offsets[0], record0.Length);
        for (var i = 0; i < records.Length; i++)
        {
            WriteU32(pdb, 78 + 8 * (i + 1), (uint)offsets[i + 1]);
            Array.Copy(records[i], 0, pdb, offsets[i + 1], records[i].Length);
        }
        return pdb;
    }

    private static void WriteU16(byte[] b, int off, ushort v) { b[off] = (byte)(v >> 8); b[off + 1] = (byte)v; }
    private static void WriteU32(byte[] b, int off, uint v)
    {
        b[off] = (byte)(v >> 24); b[off + 1] = (byte)(v >> 16);
        b[off + 2] = (byte)(v >> 8); b[off + 3] = (byte)v;
    }

    // ─── 7. Форматы: CBZ без текстового слоя ───

    [Fact]
    public void Cbz_WithoutComicInfo_ReturnsEmpty()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, true))
        {
            var entry = zip.CreateEntry("page001.png");
            using var w = entry.Open();
            w.Write([1, 2, 3, 4]);
        }
        ms.Position = 0;
        var text = DocumentReader.Extract("comic.cbz", ms.ToArray());
        Assert.Equal(string.Empty, text);
    }

    // ─── 8. Пер-паттернные папки отчётов ───

    [Fact]
    public void PerPatternFolders_WriteTxtCsvJsonPerId()
    {
        var matches = new List<SemanticMatch>
        {
            new("temporal-title", "Названия периодов", @"C:\c\a.txt", 5, "Год Красной Луны"),
            new("temporal-title", "Названия периодов", @"C:\c\b.txt", 12, "Час Воющей Собаки"),
            new("ritual-formula", "Ритуальные формулы", @"C:\c\a.txt", 40, "древний Наг обряд"),
        };
        var root = Path.Combine(Path.GetTempPath(), "ea_pat_" + Guid.NewGuid().ToString("N"));

        try
        {
            ReportExporter.WritePerPatternFolders(matches, root, null);

            var tt = Path.Combine(root, "temporal-title");
            Assert.True(File.Exists(Path.Combine(tt, "matches.txt")));
            Assert.True(File.Exists(Path.Combine(tt, "matches.csv")));
            Assert.True(File.Exists(Path.Combine(tt, "matches.json")));
            Assert.True(File.Exists(Path.Combine(root, "ritual-formula", "matches.txt")));

            var txt = File.ReadAllText(Path.Combine(tt, "matches.txt"));
            Assert.Contains("Год Красной Луны", txt);
            Assert.DoesNotContain("древний Наг", txt); // чужой паттерн не попал

            var json = File.ReadAllText(Path.Combine(tt, "matches.json"));
            Assert.Contains("\"pattern\": \"temporal-title\"", json);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    // ─── 9. Обособленный скан паттернов ───

    [Fact]
    public void ScanPatternsOnly_FindsMatchesWithoutEntityAnalysis()
    {
        var corpus = Path.Combine(Path.GetTempPath(), "ea_scan_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(corpus);
        File.WriteAllText(Path.Combine(corpus, "chronicle.txt"),
            "В хрониках говорится: Год Красной Луны наступил внезапно.\n" +
            "А затем пришёл Час Воющей Собаки.\n",
            new UTF8Encoding(false));

        try
        {
            var options = new AnalyzerOptions();
            options.Corpus.Dir = corpus;
            options.Output.Dir = Path.Combine(corpus, "_out");

            var codex = DefaultCodex.Load();
            var definitions = CodexSearchAdapter.ToDefinitions(codex).ToList();
            var catalog = new EntityCatalog(definitions, generateVariants: true);
            var engine = new AnalysisEngine(options, catalog, "light");
            var result = engine.ScanPatternsOnly(CancellationToken.None);

            Assert.Equal(1, result.FilesScanned);
            Assert.Contains(result.Matches, m => m.PatternId == "temporal-title");
            Assert.Contains(result.Matches, m => m.Snippet.Contains("Красной"));
        }
        finally
        {
            Directory.Delete(corpus, recursive: true);
        }
    }
}

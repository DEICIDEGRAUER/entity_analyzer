using System.Text.RegularExpressions;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

// ═══════════════ R1-28: Академические отчёты («научное резюме») ═══════════════
// Резюме строится ТОЛЬКО из содержимого готового пакета .earepro (манифест +
// запись прогона + хвост журнала + инвентаризация): рецензент без доступа к
// корпусу и %APPDATA% автора получает публикационно-готовый Markdown с
// методологией, результатами и независимой проверкой цепочки целостности.
// Все числа — в инвариантной культуре: документ не зависит от локали машины.

public class AcademicReportTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ea_acad_" + Guid.NewGuid().ToString("N"));

    public AcademicReportTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string TempPath(string ext) => Path.Combine(_dir, $"pkg_{Guid.NewGuid():N}{ext}");

    private static RunRecord MakeRecord(int mentions) => new()
    {
        StartedAt = DateTimeOffset.Parse("2026-08-25T12:00:00Z"),
        Preset = "titanic",
        TotalMentions = mentions,
        FilesTotal = 3,
        FilesWithMentions = 2,
        RawMatches = 40,
        FinalMentions = mentions,
        UniqueEntities = 2,
        DurationSeconds = 1.5,
        BytesScanned = 5 * 1024 * 1024,
        CorpusDirs = [@"C:\корпус"],
        EntityCounts = new Dictionary<string, int> { ["Ктулху"] = 12, ["Йеб"] = mentions - 12 },
        Status = "Успешно",
    };

    /// <summary>Журнал с корректной цепочкой prev_hash → hash.</summary>
    private static List<RunRecord> ChainedJournal(params RunRecord[] records)
    {
        var list = new List<RunRecord>();
        var prev = JournalIntegrity.Genesis;
        foreach (var r in records)
        {
            r.PrevHash = prev;
            r.Hash = JournalIntegrity.ComputeHash(r, prev);
            prev = r.Hash;
            list.Add(r);
        }
        return list;
    }

    private string SavePackage(int mentions,
        IReadOnlyList<RunRecord>? journal = null,
        IReadOnlyList<FileInventoryEntry>? inventory = null,
        bool chainValid = true)
    {
        var path = TempPath(ReproPackage.Extension);
        var run = MakeRecord(mentions);
        ReproPackage.Save(path,
            new ReproManifest(DateTimeOffset.Parse("2026-08-25T13:00:00Z"), "1.0.0", "titanic",
                TotalMentions: mentions, FilesTotal: 3,
                JournalRecords: journal?.Count ?? 0, ChainValid: chainValid),
            run,
            journalTail: journal ?? ChainedJournal(run),
            settingsJson: null, codexJson: null, patternsJson: null,
            inventory: inventory ?? [new FileInventoryEntry(@"C:\корпус\миф.txt", "abc123", 77)]);
        return path;
    }

    // ─────────────── ReproPackage.LoadFull ───────────────

    [Fact]
    public void LoadFull_roundtrips_all_sections()
    {
        var run = MakeRecord(10);
        var earlier = MakeRecord(5);
        var path = TempPath(ReproPackage.Extension);
        ReproPackage.Save(path,
            new ReproManifest(DateTimeOffset.UtcNow, "1.0.0", "titanic", 10, 3, 2, true),
            run, [earlier, run], null, null, null,
            [new FileInventoryEntry(@"C:\c\a.txt", "deadbeef", 12)]);

        var full = ReproPackage.LoadFull(path);

        Assert.Equal("titanic", full.Manifest.Preset);
        Assert.NotNull(full.Run);
        Assert.Equal(10, full.Run!.TotalMentions);
        Assert.Equal(2, full.Journal.Count);
        Assert.Single(full.Inventory);
        Assert.Equal("deadbeef", full.Inventory[0].Sha256);
        Assert.Equal(12, full.Inventory[0].Bytes);
    }

    // ─────────────── Структура и метаданные резюме ───────────────

    [Fact]
    public void Summary_contains_title_metadata_and_abstract_numbers()
    {
        var pkg = SavePackage(mentions: 20);
        var md = File.ReadAllText(AcademicReport.Write(pkg, _dir));

        Assert.Contains("# Научное резюме анализа корпуса", md);
        Assert.Contains("titanic", md);
        Assert.Contains("2026-08-25", md);
        Assert.Contains("## Аннотация", md);
        Assert.Contains("выявил 20 итоговых цитат", md);
        // Воронка в аннотации: raw → final.
        Assert.Contains("40", md);
    }

    [Fact]
    public void Methodology_section_reports_configuration_rows()
    {
        var pkg = SavePackage(mentions: 20);
        var md = File.ReadAllText(AcademicReport.Write(pkg, _dir));

        Assert.Contains("## 1. Методология", md);
        Assert.Contains("| Пресет | titanic |", md);
        Assert.Contains("| Файлов в корпусе | 3 |", md);
        Assert.Contains("| Файлов с находками | 2 |", md);
        Assert.Contains("| Сырых совпадений автомата | 40 |", md);
        Assert.Contains("| Итоговых цитат | 20 |", md);
        Assert.Contains("| Статус прогона | Успешно |", md);
        Assert.Contains("| Длительность, c | 1.5 |", md);
        Assert.Contains("МБ", md);
    }

    [Fact]
    public void Stage_timings_listed_when_present()
    {
        var path = TempPath(ReproPackage.Extension);
        var run = MakeRecord(10);
        run.StageMs = new Dictionary<string, double> { ["scan"] = 120.5, ["filters"] = 30.25 };
        ReproPackage.Save(path,
            new ReproManifest(DateTimeOffset.UtcNow, "1.0.0", "titanic", 10, 3, 1, true),
            run, [run], null, null, null, []);

        var md = File.ReadAllText(AcademicReport.Write(path, _dir));

        Assert.Contains("Тайминги стадий", md);
        Assert.Contains("scan", md);
        Assert.Contains("120.5", md);
    }

    // ─────────────── Результаты ───────────────

    [Fact]
    public void Results_table_lists_entity_counts_sorted_desc()
    {
        var pkg = SavePackage(mentions: 20); // Ктулху=12, Йеб=8
        var md = File.ReadAllText(AcademicReport.Write(pkg, _dir));

        Assert.Contains("## 2. Результаты", md);
        Assert.Contains("| Ктулху | 12 |", md);
        Assert.Contains("| Йеб | 8 |", md);
        Assert.True(md.IndexOf("| Ктулху | 12 |", StringComparison.Ordinal)
                    < md.IndexOf("| Йеб | 8 |", StringComparison.Ordinal),
            "более частотная сущность должна идти первой");
    }

    [Fact]
    public void Pipe_in_entity_name_is_escaped_in_table()
    {
        var path = TempPath(ReproPackage.Extension);
        var run = MakeRecord(6);
        run.EntityCounts = new Dictionary<string, int> { ["A|B"] = 3 };
        ReproPackage.Save(path,
            new ReproManifest(DateTimeOffset.UtcNow, "1.0.0", "titanic", 6, 3, 1, true),
            run, [run], null, null, null, []);

        var md = File.ReadAllText(AcademicReport.Write(path, _dir));

        Assert.Contains("A\\|B", md); // канал экранирован — таблица не ломается
    }

    [Fact]
    public void Missing_entity_dictionary_renders_placeholder()
    {
        var path = TempPath(ReproPackage.Extension);
        var run = MakeRecord(0);
        run.EntityCounts = null;
        ReproPackage.Save(path,
            new ReproManifest(DateTimeOffset.UtcNow, "1.0.0", "minimal", 0, 3, 1, true),
            run, [run], null, null, null, []);

        var md = File.ReadAllText(AcademicReport.Write(path, _dir));

        Assert.Contains("нет словаря сущностей", md);
    }

    // ─────────────── Целостность и воспроизводимость ───────────────

    [Fact]
    public void Integrity_section_reports_valid_chain_and_package_hash()
    {
        var pkg = SavePackage(mentions: 20);
        var expectedHash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(pkg))).ToLowerInvariant();

        var md = File.ReadAllText(AcademicReport.Write(pkg, _dir));

        Assert.Contains("## 3. Воспроизводимость и целостность", md);
        Assert.Contains("цепочки журнала: целостна", md);
        Assert.Matches(new Regex(@"SHA-256 пакета: [0-9a-f]{64}"), md);
        Assert.Contains(expectedHash, md);
        // Хеши звена прогона из журнала попадают в документ.
        Assert.Contains("Хеш записи прогона:", md);
    }

    [Fact]
    public void Integrity_section_detects_broken_chain_independently_of_manifest()
    {
        // Автор заявляет «целостна», но журнал в пакете подделан:
        // у второй записи prev_hash не совпадает с хешем первой.
        var r1 = MakeRecord(5);
        r1.PrevHash = JournalIntegrity.Genesis;
        r1.Hash = JournalIntegrity.ComputeHash(r1, r1.PrevHash);
        var r2 = MakeRecord(7);
        r2.PrevHash = new string('f', 64);
        r2.Hash = JournalIntegrity.ComputeHash(r2, r2.PrevHash);

        var pkg = SavePackage(mentions: 7, journal: [r1, r2], chainValid: true);
        var md = File.ReadAllText(AcademicReport.Write(pkg, _dir));

        Assert.Contains("цепочки журнала: НАРУШЕНА", md);
    }

    [Fact]
    public void Inventory_aggregate_lists_count_and_total_bytes()
    {
        var pkg = SavePackage(mentions: 20, inventory:
        [
            new FileInventoryEntry(@"C:\к\a.txt", "h1", 77),
            new FileInventoryEntry(@"C:\к\b.txt", "h2", 23),
        ]);
        var md = File.ReadAllText(AcademicReport.Write(pkg, _dir));

        Assert.Contains("| Файлов в инвентаризации | 2 |", md);
        Assert.Contains("| Суммарный объём инвентаризации, байт | 100 |", md);
    }

    // ─────────────── Ограничения и цитирование ───────────────

    [Fact]
    public void Limitations_and_citation_sections_render()
    {
        var pkg = SavePackage(mentions: 20);
        var md = File.ReadAllText(AcademicReport.Write(pkg, _dir));

        Assert.Contains("## 4. Ограничения", md);
        Assert.Contains("## Как цитировать", md);
        Assert.Contains("Entity Analyzer", md);
        Assert.Contains(Path.GetFileName(pkg), md);
    }

    // ─────────────── Ошибки ввода ───────────────

    [Fact]
    public void Missing_package_throws_file_not_found()
    {
        Assert.Throws<FileNotFoundException>(
            () => AcademicReport.Write(TempPath(ReproPackage.Extension), _dir));
    }

    [Fact]
    public void Output_defaults_to_package_directory_when_dir_omitted()
    {
        var pkg = SavePackage(mentions: 5);
        var written = AcademicReport.Write(pkg); // outputDir не задан

        Assert.Equal(Path.Combine(_dir, "academic_summary.md"), written);
        Assert.True(File.Exists(written));
    }
}

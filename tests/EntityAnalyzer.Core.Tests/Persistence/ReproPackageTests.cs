using System.IO.Compression;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

// ═══════════════ R2-12: Пакет воспроизводимости .earepro ═══════════════
// Один ZIP для рецензента: манифест, запись прогона с хешами, хвост журнала,
// снимки настроек/кодекса/паттернов и инвентаризация корпуса (пути + SHA-256).
// Рецензент проверяет: тот же корпус (хеши файлов), та же конфигурация,
// журнал не подделан (цепочка).

public class FileInventoryTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ea_inv_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Write(string name, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_dir, name))!);
        File.WriteAllText(Path.Combine(_dir, name), content);
        return Path.Combine(_dir, name);
    }

    [Fact]
    public void Hashes_are_stable_and_content_sensitive()
    {
        var a1 = Write("a.txt", "первый текст");
        var a2 = Write("copy/a.txt", "первый текст");

        var inv = FileInventory.Build([_dir], [".txt"]);

        Assert.Equal(2, inv.Count);
        Assert.Equal(inv[0].Sha256, inv[1].Sha256);          // одинаковое содержимое
        Assert.NotEqual(a1, a2);                              // разные пути

        File.WriteAllText(Write("a.txt", "другой текст"), "другой текст");
        var inv2 = FileInventory.Build([_dir], [".txt"]);
        // Хеш изменился вместе с содержимым.
        Assert.NotEqual(inv[0].Sha256, inv2.First(e => e.FullPath.EndsWith("a.txt")).Sha256);
    }

    [Fact]
    public void Extension_filter_and_missing_dirs()
    {
        Write("text.txt", "текст");
        Write("image.png", "не текст");

        var inv = FileInventory.Build([_dir, Path.Combine(_dir, "нет_такого")], [".txt"]);

        Assert.Single(inv);
        Assert.EndsWith("text.txt", inv[0].FullPath);
    }

    [Fact]
    public void Bytes_recorded()
    {
        Write("a.txt", "12345");
        var inv = FileInventory.Build([_dir], [".txt"]);
        Assert.Equal(5, inv[0].Bytes);
    }

    [Fact]
    public void Build_excludes_output_directory_tree()
    {
        // BUG (найден при R1-28 E2E): выходная папка внутри корпуса попадала
        // в инвентаризацию пакета — хеши собственных отчётов загрязняли
        // доказательство корпуса (9 файлов вместо 1).
        Write("миф.txt", "корпус");
        Write("_out/entities_report.txt", "собственный отчёт");
        Write("_out/nested/deep.txt", "вложенный отчёт");

        var inv = FileInventory.Build([_dir], [".txt"],
            excludeDir: Path.Combine(_dir, "_out"));

        Assert.Single(inv);
        Assert.EndsWith("миф.txt", inv[0].FullPath);
    }

    [Fact]
    public void ExcludeDir_is_optional_and_null_safe()
    {
        Write("a.txt", "1");
        Assert.Single(FileInventory.Build([_dir], [".txt"], excludeDir: null));
        // Несуществующая папка исключения не ломает обход.
        Assert.Single(FileInventory.Build([_dir], [".txt"],
            excludeDir: Path.Combine(_dir, "нет_такой")));
    }
}

public class ReproPackageTests
{
    private static RunRecord MakeRecord(int mentions, string? prev = null) => new()
    {
        StartedAt = DateTimeOffset.Parse("2026-08-25T12:00:00Z"),
        Preset = "titanic",
        TotalMentions = mentions,
        FilesTotal = 3,
        FilesWithMentions = 2,
        RawMatches = 40,
        FinalMentions = mentions,
        UniqueEntities = 5,
        PrevHash = prev,
    };

    [Fact]
    public void Package_contains_all_sections()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ea_repro_{Guid.NewGuid():N}.earepro");
        try
        {
            var run = MakeRecord(10);
            ReproPackage.Save(path,
                new ReproManifest(DateTimeOffset.UtcNow, "v18-net", "titanic",
                    TotalMentions: 10, FilesTotal: 3, JournalRecords: 1, ChainValid: true),
                run,
                journalTail: [run],
                settingsJson: "{\"a\":1}",
                codexJson: null,
                patternsJson: "{\"p\":[]}",
                inventory: [new FileInventoryEntry(@"C:\c\a.txt", "deadbeef", 12)]);

            using var zip = ZipFile.OpenRead(path);
            var names = zip.Entries.Select(e => e.FullName).ToHashSet();
            Assert.Subset(names, new HashSet<string>
            {
                "manifest.json", "run.json", "journal.jsonl", "inventory.csv",
            });
            Assert.Contains(names, n => n == "settings.json");
            Assert.DoesNotContain(names, n => n == "codex.json"); // null-секция пропущена
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Manifest_roundtrips_with_chain_flag()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ea_repro_{Guid.NewGuid():N}.earepro");
        try
        {
            var manifest = new ReproManifest(DateTimeOffset.Parse("2026-08-25T10:00:00Z"),
                "v18-net", "minimal", 42, 7, 9, ChainValid: false);
            ReproPackage.Save(path, manifest, MakeRecord(42), [], null, null, null, []);

            var loaded = ReproPackage.Load(path);
            Assert.Equal(manifest.Preset, loaded.Manifest.Preset);
            Assert.Equal(42, loaded.Manifest.TotalMentions);
            Assert.False(loaded.Manifest.ChainValid);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Journal_tail_is_valid_jsonl()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ea_repro_{Guid.NewGuid():N}.earepro");
        try
        {
            var r1 = MakeRecord(1);
            var r2 = MakeRecord(2);
            ReproPackage.Save(path,
                new ReproManifest(DateTimeOffset.UtcNow, "v18-net", "titanic", 3, 6, 2, true),
                r2, [r1, r2], null, null, null, []);

            using var zip = ZipFile.OpenRead(path);
            using var stream = zip.GetEntry("journal.jsonl")!.Open();
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);

            Assert.Equal(2, lines.Length);
            Assert.Contains("\"totalMentions\":2", lines[1].Replace(" ", ""));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Inventory_csv_has_hash_column()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ea_repro_{Guid.NewGuid():N}.earepro");
        try
        {
            ReproPackage.Save(path,
                new ReproManifest(DateTimeOffset.UtcNow, "v18-net", "titanic", 0, 0, 0, true),
                MakeRecord(0), [], null, null, null,
                [new FileInventoryEntry(@"C:\корпус\миф.txt", "abc123", 77)]);

            using var zip = ZipFile.OpenRead(path);
            using var stream = zip.GetEntry("inventory.csv")!.Open();
            using var reader = new StreamReader(stream);
            var csv = reader.ReadToEnd();

            Assert.Contains("abc123", csv);
            Assert.Contains("миф.txt", csv);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Load_without_manifest_throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ea_bad_{Guid.NewGuid():N}.zip");
        try
        {
            using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("чужое.txt");
                using var s = e.Open();
                s.Write("x"u8.ToArray());
            }
            Assert.Throws<InvalidDataException>(() => ReproPackage.Load(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

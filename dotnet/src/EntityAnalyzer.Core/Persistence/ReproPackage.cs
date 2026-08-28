using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace EntityAnalyzer.Core.Persistence;

/// <summary>Манифест пакета воспроизводимости .earepro (R2-12).</summary>
public sealed record ReproManifest(
    DateTimeOffset CreatedAt,
    string EngineVersion,
    string Preset,
    int TotalMentions,
    int FilesTotal,
    int JournalRecords,
    bool ChainValid);

/// <summary>Распакованный пакет: манифест + запись прогона (null — секции нет).</summary>
public sealed record ReproPackageContent(ReproManifest Manifest, RunRecord? Run);

/// <summary>
/// R1-28: полная распаковка пакета — манифест, запись прогона, хвост журнала
/// и инвентаризация корпуса. Нужен академическому отчёту, строящему резюме
/// исключительно из содержимого пакета.
/// </summary>
public sealed record ReproPackageFull(
    ReproManifest Manifest,
    RunRecord? Run,
    IReadOnlyList<RunRecord> Journal,
    IReadOnlyList<Analysis.FileInventoryEntry> Inventory);

/// <summary>
/// Пакет воспроизводимости (R2-12): один ZIP для рецензента — прогон с хешами
/// цепочки журнала, снимки конфигурации и инвентаризация корпуса (пути +
/// SHA-256). Рецензент без доступа к данным автора проверяет: тот же корпус
/// (совпадение хешей файлов), та же конфигурация, журнал не подделан.
/// Формат прозрачен: обычный ZIP, journal.jsonl — в формате HistoryStore.
/// </summary>
public static class ReproPackage
{
    public const string Extension = ".earepro";

    private const string EntryManifest = "manifest.json";
    private const string EntryRun = "run.json";
    private const string EntryJournal = "journal.jsonl";
    private const string EntryInventory = "inventory.csv";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions JsonLineOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Сохранить пакет. Секции null пропускаются; запись атомарна.</summary>
    public static void Save(string path, ReproManifest manifest, RunRecord run,
        IReadOnlyList<RunRecord>? journalTail, string? settingsJson, string? codexJson,
        string? patternsJson, IReadOnlyList<Analysis.FileInventoryEntry>? inventory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var tmp = path + ".tmp";
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            WriteEntry(zip, EntryManifest, JsonSerializer.Serialize(manifest, JsonOpts));
            WriteEntry(zip, EntryRun, JsonSerializer.Serialize(run, JsonOpts));

            var sb = new StringBuilder();
            foreach (var r in journalTail ?? [])
                sb.AppendLine(JsonSerializer.Serialize(r, JsonLineOpts));
            WriteEntry(zip, EntryJournal, sb.ToString());

            if (settingsJson is not null) WriteEntry(zip, "settings.json", settingsJson);
            if (codexJson is not null) WriteEntry(zip, "codex.json", codexJson);
            if (patternsJson is not null) WriteEntry(zip, "semantic_patterns.json", patternsJson);

            if (inventory is not null)
            {
                var csv = new StringBuilder("path;sha256;bytes\n");
                foreach (var e in inventory)
                    csv.Append(e.FullPath.Replace(';', ',')).Append(';')
                       .Append(e.Sha256).Append(';').Append(e.Bytes).Append('\n');
                WriteEntry(zip, EntryInventory, csv.ToString());
            }
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Загрузить манифест и запись прогона. Без манифеста — InvalidDataException.</summary>
    public static ReproPackageContent Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var manifestEntry = zip.GetEntry(EntryManifest)
            ?? throw new InvalidDataException($"В пакете нет {EntryManifest} — это не пакет воспроизводимости");

        var manifest = JsonSerializer.Deserialize<ReproManifest>(ReadEntry(manifestEntry), JsonOpts)
            ?? throw new InvalidDataException("Манифест пакета не читается");

        RunRecord? run = null;
        var runEntry = zip.GetEntry(EntryRun);
        if (runEntry is not null)
            run = JsonSerializer.Deserialize<RunRecord>(ReadEntry(runEntry), JsonOpts);

        return new ReproPackageContent(manifest, run);
    }

    /// <summary>
    /// R1-28: загрузить все секции пакета. Без манифеста — InvalidDataException.
    /// Журнал читается построчно (терпимо к битым строкам — как HistoryStore),
    /// инвентаризация — из CSV «path;sha256;bytes» (путь без ';' — см. Save).
    /// </summary>
    public static ReproPackageFull LoadFull(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var manifestEntry = zip.GetEntry(EntryManifest)
            ?? throw new InvalidDataException($"В пакете нет {EntryManifest} — это не пакет воспроизводимости");
        var manifest = JsonSerializer.Deserialize<ReproManifest>(ReadEntry(manifestEntry), JsonOpts)
            ?? throw new InvalidDataException("Манифест пакета не читается");

        RunRecord? run = null;
        var runEntry = zip.GetEntry(EntryRun);
        if (runEntry is not null)
            run = JsonSerializer.Deserialize<RunRecord>(ReadEntry(runEntry), JsonOpts);

        var journal = new List<RunRecord>();
        var journalEntry = zip.GetEntry(EntryJournal);
        if (journalEntry is not null)
        {
            foreach (var raw in ReadEntry(journalEntry).Split('\n'))
            {
                // AppendLine в Save пишет Environment.NewLine — срезаем '\r'.
                var line = raw.TrimEnd('\r');
                if (line.Length == 0) continue;
                try
                {
                    if (JsonSerializer.Deserialize<RunRecord>(line, JsonLineOpts) is { } rec)
                        journal.Add(rec);
                }
                catch { /* битая строка журнала не роняет чтение пакета */ }
            }
        }

        var inventory = new List<Analysis.FileInventoryEntry>();
        var invEntry = zip.GetEntry(EntryInventory);
        if (invEntry is not null)
        {
            foreach (var raw in ReadEntry(invEntry).Split('\n'))
            {
                if (raw.Length == 0 || raw.StartsWith("path;", StringComparison.Ordinal)) continue;
                var parts = raw.TrimEnd('\r').Split(';');
                if (parts.Length >= 3 && long.TryParse(parts[2], NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out var bytes))
                    inventory.Add(new Analysis.FileInventoryEntry(parts[0], parts[1], bytes));
            }
        }

        return new ReproPackageFull(manifest, run, journal, inventory);
    }

    private static void WriteEntry(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private static string ReadEntry(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

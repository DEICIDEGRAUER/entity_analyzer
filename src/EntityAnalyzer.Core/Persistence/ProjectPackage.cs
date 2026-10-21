using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace EntityAnalyzer.Core.Persistence;

/// <summary>
/// Манифест исследования внутри .eaproj-пакета (R2-11).
/// </summary>
public sealed record ProjectManifest(
    DateTimeOffset CreatedAt,
    string EngineVersion,
    string CorpusDir,
    string Preset,
    string Notes = "");

/// <summary>Распакованное содержимое .eaproj: манифест + JSON-секции (null — секции нет).</summary>
public sealed record ProjectPackageContent(
    ProjectManifest Manifest,
    string? CodexJson,
    string? SemanticPatternsJson,
    string? SettingsJson);

/// <summary>
/// «Исследование как файл» (R2-11): единый ZIP-пакет .eaproj со ссылками на
/// корпус, кодексом, семантическими паттернами и настройками прогона.
/// Решает боль переноса: один файл вместо ручного копирования четырёх по памяти.
/// Формат намеренно прозрачен — обычный ZIP, читается любым архиватором.
/// </summary>
public static class ProjectPackage
{
    public const string Extension = ".eaproj";

    private const string EntryManifest = "manifest.json";
    private const string EntryCodex = "codex.json";
    private const string EntryPatterns = "semantic_patterns.json";
    private const string EntrySettings = "settings.json";

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Сохранить исследование в пакет. Секции null пропускаются.</summary>
    public static void Save(string path, ProjectManifest manifest,
        string? codexJson, string? semanticPatternsJson, string? settingsJson)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // Атомарная запись: сначала во временный файл, затем переименование.
        var tmp = path + ".tmp";
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            WriteEntry(zip, EntryManifest, JsonSerializer.Serialize(manifest, JsonOpts));
            if (codexJson is not null) WriteEntry(zip, EntryCodex, codexJson);
            if (semanticPatternsJson is not null) WriteEntry(zip, EntryPatterns, semanticPatternsJson);
            if (settingsJson is not null) WriteEntry(zip, EntrySettings, settingsJson);
        }
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>Загрузить пакет. Отсутствует манифест или архив повреждён — InvalidDataException.</summary>
    public static ProjectPackageContent Load(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var manifestEntry = zip.GetEntry(EntryManifest)
            ?? throw new InvalidDataException($"В пакете нет {EntryManifest} — это не файл исследования .eaproj");

        var manifest = JsonSerializer.Deserialize<ProjectManifest>(ReadEntry(manifestEntry), JsonOpts)
            ?? throw new InvalidDataException("Манифест пакета не читается");

        return new ProjectPackageContent(
            manifest,
            ReadOptional(zip, EntryCodex),
            ReadOptional(zip, EntryPatterns),
            ReadOptional(zip, EntrySettings));
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

    private static string? ReadOptional(ZipArchive zip, string name)
    {
        var entry = zip.GetEntry(name);
        return entry is null ? null : ReadEntry(entry);
    }
}

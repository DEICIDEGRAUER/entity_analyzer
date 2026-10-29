using System.Security.Cryptography;
using System.Text;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>
/// File-level source-quality override loaded from <c>entities_source_quality.csv</c>.
///
/// The sidecar is deliberately conservative: when it is present it must cover
/// every analysed file. A file that is missing from the sidecar becomes
/// <see cref="SourceQuality.Unknown"/>, never <c>usable</c>. Without a sidecar
/// the old detector-only behaviour is preserved.
/// </summary>
public sealed class SourceQualitySidecar
{
    public const string FileName = "entities_source_quality.csv";

    private readonly IReadOnlyDictionary<string, SourceQualityResult> _byFile;

    public static SourceQualitySidecar Disabled { get; } = new(
        new Dictionary<string, SourceQualityResult>(StringComparer.OrdinalIgnoreCase),
        path: null, sha256: null);

    private SourceQualitySidecar(
        IReadOnlyDictionary<string, SourceQualityResult> byFile,
        string? path,
        string? sha256)
    {
        _byFile = byFile;
        Path = path;
        Sha256 = sha256;
    }

    /// <summary>True when a sidecar was loaded; missing entries then mean unknown.</summary>
    public bool IsEnabled => Path is not null;

    /// <summary>Absolute path of the loaded sidecar, or null for detector-only mode.</summary>
    public string? Path { get; }

    /// <summary>SHA-256 of the sidecar bytes; useful in run reports and diagnostics.</summary>
    public string? Sha256 { get; }

    /// <summary>Number of file entries in the loaded sidecar.</summary>
    public int Count => _byFile.Count;

    /// <summary>Resolve a file's source quality. Sidecar wins over the detector.</summary>
    public SourceQualityResult Resolve(string fileName, SourceQualityResult detector)
    {
        if (!IsEnabled) return detector;

        var key = NormalizeKey(fileName);
        return _byFile.TryGetValue(key, out var value)
            ? value
            : new SourceQualityResult(SourceQuality.Unknown, SourceQuality.ReasonSidecarMissing);
    }

    /// <summary>
    /// Load and validate a sidecar. Required columns are <c>file</c> and
    /// <c>source_quality</c>; <c>source_reason</c> is optional. Extra columns
    /// from the documented contract are ignored.
    /// </summary>
    public static SourceQualitySidecar Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = System.IO.Path.GetFullPath(path);
        var bytes = File.ReadAllBytes(fullPath);
        var text = Decode(bytes);
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        if (lines.Length == 0)
            throw new InvalidDataException($"Source-quality sidecar is empty: {fullPath}");

        var header = ParseCsvLine(lines[0], DetectDelimiter(lines[0]));
        var delimiter = DetectDelimiter(lines[0]);
        var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < header.Count; i++)
            index[header[i].Trim()] = i;

        if (!index.TryGetValue("file", out var fileIndex) ||
            !index.TryGetValue("source_quality", out var qualityIndex))
            throw new InvalidDataException(
                "Source-quality sidecar must have 'file' and 'source_quality' columns.");

        var reasonIndex = index.TryGetValue("source_reason", out var r) ? r : -1;
        var map = new Dictionary<string, SourceQualityResult>(StringComparer.OrdinalIgnoreCase);
        for (var lineNo = 2; lineNo <= lines.Length; lineNo++)
        {
            var fields = ParseCsvLine(lines[lineNo - 1], delimiter);
            var file = Field(fields, fileIndex).Trim();
            if (file.Length == 0)
                throw new InvalidDataException($"Source-quality sidecar row {lineNo} has empty 'file'.");

            var quality = NormalizeQuality(Field(fields, qualityIndex));
            var reason = reasonIndex >= 0 ? Field(fields, reasonIndex).Trim() : "";
            if (reason.Length == 0) reason = DefaultReason(quality);

            var key = NormalizeKey(file);
            var value = new SourceQualityResult(quality, reason);
            if (map.TryGetValue(key, out var existing) && existing != value)
                throw new InvalidDataException(
                    $"Source-quality sidecar has conflicting duplicate entry for '{file}'.");

            map[key] = value;
        }

        if (map.Count == 0)
            throw new InvalidDataException("Source-quality sidecar has no data rows.");

        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return new SourceQualitySidecar(map, fullPath, hash);
    }

    private static string Decode(byte[] bytes)
    {
        // UTF-8 is the contract. A UTF-8 BOM is consumed explicitly so it does
        // not leak into the first header name.
        var offset = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        return new UTF8Encoding(false, true).GetString(bytes, offset, bytes.Length - offset);
    }

    private static char DetectDelimiter(string header) =>
        header.StartsWith("file;", StringComparison.OrdinalIgnoreCase) ? ';' : ',';

    private static string Field(IReadOnlyList<string> fields, int index) =>
        index >= 0 && index < fields.Count ? fields[index] : "";

    private static string NormalizeKey(string fileName) =>
        System.IO.Path.GetFileName(fileName.Trim());

    private static string NormalizeQuality(string value)
    {
        var quality = value.Trim().ToLowerInvariant();
        return quality switch
        {
            SourceQuality.Usable or SourceQuality.DegradedLexical or
            SourceQuality.DegradedOcr or SourceQuality.Unknown => quality,
            _ => throw new InvalidDataException(
                $"Unknown source_quality '{value}'. Expected usable, degraded_lexical, degraded_ocr or unknown."),
        };
    }

    private static string DefaultReason(string quality) => quality switch
    {
        SourceQuality.DegradedLexical => SourceQuality.ReasonLexicalSubstitution,
        SourceQuality.DegradedOcr => SourceQuality.ReasonOcrMissingSpace,
        SourceQuality.Unknown => SourceQuality.ReasonCandidateTail,
        _ => SourceQuality.ReasonDetectorNegative,
    };

    /// <summary>Minimal RFC 4180 line parser (quoted cells and doubled quotes).</summary>
    private static List<string> ParseCsvLine(string line, char delimiter)
    {
        var fields = new List<string>();
        var value = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { value.Append('"'); i++; }
                    else quoted = false;
                }
                else value.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == delimiter) { fields.Add(value.ToString()); value.Clear(); }
            else value.Append(c);
        }
        if (quoted)
            throw new InvalidDataException("Unterminated quoted field in source-quality sidecar.");
        fields.Add(value.ToString());
        return fields;
    }
}

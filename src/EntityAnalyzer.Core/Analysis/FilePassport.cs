using System.Text.RegularExpressions;
using EntityAnalyzer.Core.IO;
using EntityAnalyzer.Core.Text;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>Группа 3 (item 24): сущность и её статистика внутри одного файла.</summary>
public sealed record PassportEntity(
    string Entity,
    int Count,
    int Exact,
    int Fuzzy,
    int Repeats,
    IReadOnlyList<string> Variants);

/// <summary>Сработавший в файле семантический паттерн.</summary>
public sealed record PassportPattern(string PatternName, int Count, string Example);

/// <summary>Тема файла: сколько упоминаний попало в неё (Share — % от всех упоминаний).</summary>
public sealed record PassportTheme(string Theme, int Count, double Share);

/// <summary>Глава (или сегмент) файла с плотностью упоминаний.</summary>
public sealed record PassportChapter(
    string Label,
    long StartOffset,
    long EndOffset,
    long Chars,
    int Mentions,
    double DensityPer10k);

/// <summary>Связь с другим файлом через общие сущности.</summary>
public sealed record PassportNeighbor(
    string FileName,
    string FullPath,
    int SharedEntities,
    IReadOnlyList<string> Entities);

/// <summary>
/// Полный паспорт одного файла: агрегация уже существующих данных прогона
/// (сущности, паттерны, темы, плотность по главам, связи с другими файлами).
/// </summary>
public sealed record FilePassport(
    string FileName,
    string FullPath,
    bool Found,
    int TotalMentions,
    int TotalFound,
    int EntityCount,
    long CharCount,
    double DensityPer10k,
    IReadOnlyList<string> Tags,
    IReadOnlyList<PassportEntity> Entities,
    IReadOnlyList<PassportPattern> Patterns,
    IReadOnlyList<PassportTheme> Themes,
    IReadOnlyList<PassportChapter> Chapters,
    IReadOnlyList<PassportNeighbor> Neighbors,
    bool TextAvailable,
    bool TextAlignsWithAnalysis,
    string Note);

/// <summary>
/// Item 24 «Режим исследования одного файла»: строит паспорт по ключу файла
/// из результатов прогона. Ничего не пересчитывает по корпусу — только
/// агрегирует упоминания, семантические находки и темы. Текст файла
/// читается отдельно (для глав) и может отсутствовать.
/// </summary>
public static partial class FilePassportBuilder
{
    public const int DefaultFallbackSegments = 12;
    private const int MaxHeadings = 200;
    private const int MaxNeighbors = 200;
    private const int MaxSharedExamples = 12;

    public static FilePassport Build(
        AnalysisSession? session,
        string fullPath,
        string? normalizedText = null,
        int fallbackSegments = DefaultFallbackSegments)
    {
        var file = FindFile(session, fullPath);
        if (file is null)
        {
            return new FilePassport(
                Path.GetFileName(fullPath ?? ""), fullPath ?? "", Found: false,
                0, 0, 0, 0, 0, [], [], [], [], [], [],
                TextAvailable: normalizedText is not null,
                TextAlignsWithAnalysis: false,
                Note: "Файл не найден в результатах последнего анализа — запустите анализ и повторите.");
        }

        var textAvailable = normalizedText is not null;
        var textAligns = textAvailable && file.CharCount > 0 && normalizedText!.Length == file.CharCount;
        var note = "";
        if (!textAvailable)
        {
            if (file.Mentions.Count > 0)
                note = "Текст файла недоступен для чтения — главы показаны сегментами по позициям упоминаний.";
        }
        else if (!textAligns && file.CharCount > 0)
        {
            note = "Текст файла изменился после анализа — разбивка по главам и позиции могут смещаться.";
        }

        return new FilePassport(
            file.FileName,
            file.FullPath,
            Found: true,
            file.Mentions.Count,
            file.TotalFound,
            file.Mentions.Select(m => m.Entity).Distinct(StringComparer.Ordinal).Count(),
            file.CharCount,
            file.DensityPer10k,
            FileTagBuilder.TagsFor(file.Mentions),
            BuildEntities(file),
            BuildPatterns(file),
            BuildThemes(file),
            BuildChapters(file, normalizedText, fallbackSegments),
            BuildNeighbors(session, file),
            textAvailable,
            textAligns,
            note);
    }

    /// <summary>
    /// Прочитать и нормализовать текст файла так же, как движок анализа
    /// (DocumentReader.Extract → TextNormalizer.Normalize; препроцессоры в GUI не задаются).
    /// Возвращает null, если файл недоступен или слишком велик для синхронного чтения.
    /// </summary>
    public static string? TryReadNormalizedText(string fullPath, long maxBytes = 32L * 1024 * 1024)
    {
        try
        {
            var info = new FileInfo(fullPath);
            if (!info.Exists || info.Length > maxBytes) return null;
            var bytes = File.ReadAllBytes(fullPath);
            if (bytes.Length == 0) return "";
            var text = DocumentReader.Extract(fullPath, bytes);
            return TextNormalizer.Normalize(text);
        }
        catch
        {
            return null;
        }
    }

    private static FileResult? FindFile(AnalysisSession? session, string fullPath)
    {
        if (session is null || string.IsNullOrEmpty(fullPath)) return null;
        var exact = session.Files.FirstOrDefault(
            f => string.Equals(f.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return exact;

        var name = Path.GetFileName(fullPath);
        var byName = session.Files
            .Where(f => string.Equals(f.FileName, name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return byName.Count == 1 ? byName[0] : null;
    }

    private static List<PassportEntity> BuildEntities(FileResult file) =>
        [.. file.Mentions
            .GroupBy(m => m.Entity, StringComparer.Ordinal)
            .Select(g => new PassportEntity(
                g.Key,
                g.Count(),
                g.Count(m => !m.IsFuzzy),
                g.Count(m => m.IsFuzzy),
                g.Sum(m => Math.Max(1, m.Repeats)),
                [.. g.GroupBy(m => m.Variant, StringComparer.Ordinal)
                      .OrderByDescending(v => v.Count())
                      .ThenBy(v => v.Key, StringComparer.Ordinal)
                      .Select(v => v.Key)
                      .Take(10)]))
            .OrderByDescending(e => e.Count)
            .ThenBy(e => e.Entity, StringComparer.Ordinal)];

    private static List<PassportPattern> BuildPatterns(FileResult file) =>
        [.. file.SemanticMatches
            .GroupBy(m => string.IsNullOrEmpty(m.PatternName) ? m.PatternId : m.PatternName, StringComparer.Ordinal)
            .Select(g => new PassportPattern(g.Key, g.Count(), Trim(g.First().Snippet, 140)))
            .OrderByDescending(p => p.Count)
            .ThenBy(p => p.PatternName, StringComparer.Ordinal)];

    private static List<PassportTheme> BuildThemes(FileResult file)
    {
        var total = file.Mentions.Count;
        return [.. file.Mentions
            .Where(m => !string.IsNullOrEmpty(m.Theme))
            .GroupBy(m => m.Theme, StringComparer.Ordinal)
            .Select(g => new PassportTheme(
                g.Key, g.Count(),
                total > 0 ? Math.Round(g.Count() * 100.0 / total, 1) : 0))
            .OrderByDescending(t => t.Count)
            .ThenBy(t => t.Theme, StringComparer.Ordinal)];
    }

    private static List<PassportNeighbor> BuildNeighbors(AnalysisSession? session, FileResult file)
    {
        if (session is null) return [];
        var target = file.Mentions.Select(m => m.Entity).ToHashSet(StringComparer.Ordinal);
        if (target.Count == 0) return [];

        var result = new List<PassportNeighbor>();
        foreach (var other in session.Files)
        {
            if (string.Equals(other.FullPath, file.FullPath, StringComparison.OrdinalIgnoreCase)) continue;
            var shared = other.Mentions
                .Select(m => m.Entity)
                .Where(target.Contains)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(e => e, StringComparer.Ordinal)
                .ToList();
            if (shared.Count == 0) continue;
            result.Add(new PassportNeighbor(
                other.FileName, other.FullPath, shared.Count,
                [.. shared.Take(MaxSharedExamples)]));
        }

        return [.. result
            .OrderByDescending(n => n.SharedEntities)
            .ThenBy(n => n.FileName, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxNeighbors)];
    }

    // ---------- главы ----------

    private static List<PassportChapter> BuildChapters(
        FileResult file, string? text, int fallbackSegments)
    {
        var offsets = file.Mentions.Select(m => Math.Max(0, m.Offset)).ToList();
        long total = text is { Length: > 0 }
            ? text.Length
            : Math.Max(file.CharCount, offsets.Count > 0 ? offsets.Max() + 1 : 0);
        if (total <= 0) return [];

        List<(long Start, string Label)> headings = [];
        if (text is { Length: > 0 })
            headings = DetectHeadings(text);

        List<(long Start, long End, string Label)> segments = [];
        if (headings.Count > 0)
        {
            if (headings[0].Start > 0)
                segments.Add((0, headings[0].Start, "Начало"));
            for (var i = 0; i < headings.Count; i++)
            {
                var end = i + 1 < headings.Count ? headings[i + 1].Start : total;
                if (end > headings[i].Start)
                    segments.Add((headings[i].Start, end, headings[i].Label));
            }
        }
        else
        {
            var count = Math.Clamp(fallbackSegments, 1, 60);
            var size = Math.Max(1, (long)Math.Ceiling(total / (double)count));
            for (var i = 0; i < count; i++)
            {
                var start = i * size;
                if (start >= total) break;
                segments.Add((start, Math.Min(total, start + size), $"Часть {i + 1}/{count}"));
            }
        }

        var result = new List<PassportChapter>(segments.Count);
        foreach (var (start, end, label) in segments)
        {
            var mentions = offsets.Count(o => o >= start && o < end);
            var chars = Math.Max(1, end - start);
            result.Add(new PassportChapter(
                label, start, end, end - start, mentions,
                Math.Round(mentions * 10000.0 / chars, 2)));
        }
        return result;
    }

    /// <summary>Заголовки глав: «глава/часть/книга/раздел/том/chapter/part/book», римские цифры, КАПС.</summary>
    internal static List<(long Start, string Label)> DetectHeadings(string text)
    {
        var result = new List<(long Start, string Label)>();
        var pos = 0;
        var lineNo = 0;
        var lastHeadingLine = -10;
        while (pos < text.Length)
        {
            var nl = text.IndexOf('\n', pos);
            var end = nl < 0 ? text.Length : nl;
            var line = text.AsSpan(pos, end - pos).Trim();
            lineNo++;
            if (IsHeading(line))
            {
                // «Глава N» часто идёт следом за своим номером на отдельной строке —
                // не считаем такие пары двумя главами (только вплотную, через строку — уже глава).
                if (lineNo - lastHeadingLine >= 2)
                {
                    var label = line.Length > 60 ? string.Concat(line[..60], "…") : line.ToString();
                    result.Add((pos, label));
                    lastHeadingLine = lineNo;
                    if (result.Count > MaxHeadings) return [];
                }
            }
            pos = nl < 0 ? text.Length : nl + 1;
        }
        return result;
    }

    private static bool IsHeading(ReadOnlySpan<char> line)
    {
        if (line.Length == 0 || line.Length > 80) return false;
        var s = line.ToString();
        // Римские цифры проверяем до гарда длины — «I»/«V»/«X» сами по себе глава.
        if (RomanHeading().IsMatch(s)) return true;
        if (line.Length < 3) return false;
        if (KeywordHeading().IsMatch(s)) return true;

        // КАПС-заголовок: буквы есть и они все заглавные, строка короткая, без точки в конце.
        var letters = 0;
        var upper = 0;
        var words = 1;
        foreach (var ch in s)
        {
            if (char.IsLetter(ch)) { letters++; if (char.IsUpper(ch)) upper++; }
            else if (char.IsWhiteSpace(ch)) words++;
        }
        return letters >= 3 && upper == letters && words <= 8 && !s.EndsWith('.');
    }

    [GeneratedRegex(@"^\s*(глава|часть|книга|раздел|том|chapter|part|book)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex KeywordHeading();

    [GeneratedRegex(@"^\s*[IVXLCDM]{1,8}\s*[\.\)]?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex RomanHeading();

    internal static string Trim(string? s, int max)
    {
        var t = string.Join(' ', (s ?? "").Split(
            [' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        return t.Length <= max ? t : string.Concat(t.AsSpan(0, max), "…");
    }
}

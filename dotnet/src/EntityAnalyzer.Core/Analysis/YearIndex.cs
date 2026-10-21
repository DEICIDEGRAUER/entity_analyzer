using System.Text.RegularExpressions;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>Упоминание года в тексте файла (Группа 3, item 28).</summary>
public sealed record YearHit(int Year, string File, string Path, int Line, string Snippet);

/// <summary>Файл, в котором встречается год указателя.</summary>
public sealed record YearFileHits(string File, string Path, int Count, int Line, string Snippet);

/// <summary>Пункт хронологической шкалы: год, суммарная частота и файлы.</summary>
public sealed record YearEntry(int Year, int Total, IReadOnlyList<YearFileHits> Files);

/// <summary>
/// Item 28: указатель упоминаний по годам. Четырёхзначные годы (1000–2199)
/// вылавливаются из нормализованных текстов; одинаковые годы в одном файле
/// схлопываются в один пункт со счётчиком (первая строка и окрестность
/// сохраняются). Шкала строится от старого к новому. Файлы не читаются —
/// тексты передаёт вызывающая сторона.
/// </summary>
public static partial class YearIndexer
{
    public const int MinYear = 1000;
    public const int MaxYear = 2199;

    /// <summary>Потолок числа находок на файл — защита от таблиц с числами.</summary>
    public const int DefaultMaxPerFile = 200;

    public static List<YearHit> Extract(
        IReadOnlyList<CorpusText> files,
        int maxPerFile = DefaultMaxPerFile)
    {
        var hits = new List<YearHit>();
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.Text)) continue;
            var lineStarts = FragmentSearcher.LineStarts(file.Text);
            var perFile = 0;
            foreach (Match m in YearRegex().Matches(file.Text))
            {
                if (!int.TryParse(m.Value, out var year)) continue;
                if (year < MinYear || year > MaxYear) continue;
                if (++perFile > maxPerFile) break;
                hits.Add(new YearHit(
                    year,
                    file.File,
                    file.Path,
                    FragmentSearcher.LineOf(lineStarts, m.Index),
                    FragmentSearcher.Snippet(file.Text, m.Index, m.Index + m.Length)));
            }
        }
        return hits;
    }

    /// <summary>Хронологическая шкала: годы по возрастанию, файлы — по частоте.</summary>
    public static List<YearEntry> Timeline(IReadOnlyList<YearHit> hits)
    {
        return
        [
            .. hits
                .GroupBy(h => h.Year)
                .OrderBy(g => g.Key)
                .Select(g =>
                {
                    var files = g
                        .GroupBy(h => h.Path, StringComparer.OrdinalIgnoreCase)
                        .Select(fg =>
                        {
                            var first = fg.First();
                            return new YearFileHits(
                                first.File, first.Path, fg.Count(), first.Line, first.Snippet);
                        })
                        .OrderByDescending(f => f.Count)
                        .ThenBy(f => f.File, StringComparer.CurrentCulture)
                        .ToList();
                    return new YearEntry(g.Key, g.Count(), files);
                }),
        ];
    }

    /// <summary>Год как отдельное четырёхзначное число (не часть более длинного).</summary>
    [GeneratedRegex(@"(?<!\d)(?:1\d{3}|2[01]\d{2})(?!\d)", RegexOptions.CultureInvariant)]
    private static partial Regex YearRegex();
}

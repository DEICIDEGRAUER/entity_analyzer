namespace EntityAnalyzer.Core.Analysis;

/// <summary>Файл на ленте времени.</summary>
public sealed record TimelineFile(string FileName, DateTime Modified, int Mentions);

/// <summary>Месячная корзина ленты.</summary>
public sealed record TimelineMonth(string Label, int FileCount, int Mentions, IReadOnlyList<TimelineFile> Files);

/// <summary>
/// R1-10 «Timeline корпуса»: файлы группируются по месяцу даты модификации,
/// корзины сортируются хронологически — внутри месяца файлы по дате.
/// Даты подаёт вызывающий код (mtime корпуса), ядро остаётся чистым.
/// </summary>
public static class TimelineModel
{
    public static IReadOnlyList<TimelineMonth> Build(
        IEnumerable<(string FileName, DateTime Modified, int Mentions)> items)
    {
        var months = items
            .Select(x => new TimelineFile(x.FileName, x.Modified, x.Mentions))
            .GroupBy(f => f.Modified.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture))
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new TimelineMonth(
                g.Key,
                g.Count(),
                g.Sum(f => f.Mentions),
                [.. g.OrderBy(f => f.Modified).ThenBy(f => f.FileName, StringComparer.Ordinal)]))
            .ToList();
        return months;
    }
}

using EntityAnalyzer.Core.Analysis;

namespace EntityAnalyzer.Core.Themes;

/// <summary>
/// R3-1 (item 52): отрывок цитаты, попавший в тему (слова темы найдены
/// в контексте упоминания). Экспортируется с файлом и позицией.
/// </summary>
public sealed record ThemeExcerpt(
    string FileName,
    string FullPath,
    string Entity,
    string Variant,
    int Line,
    string Context,
    IReadOnlyList<string> Words);

/// <summary>
/// R3-1 (item 52): сборщик отрывков по тематическому словарю. Работает
/// на готовой сессии анализа (упоминания и контексты уже в ней) — корпус
/// повторно не сканируется и файлы не перечитываются.
/// </summary>
public static class ThemeExcerptBuilder
{
    public const int DefaultLimit = 500;

    public static List<ThemeExcerpt> Collect(
        AnalysisSession session, ThemeDictionary theme, string? entity = null, int limit = DefaultLimit)
    {
        var result = new List<ThemeExcerpt>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in session.Files)
        {
            foreach (var m in file.Mentions)
            {
                if (entity is not null && !string.Equals(m.Entity, entity, StringComparison.OrdinalIgnoreCase))
                    continue;
                var context = m.Context ?? "";
                var words = ThemeMatcher.MatchWords(context, theme.Words);
                if (words.Count == 0) continue;
                if (!seen.Add($"{file.FullPath}|{m.Line}|{m.Entity}|{m.Variant}")) continue;
                result.Add(new ThemeExcerpt(file.FileName, file.FullPath, m.Entity, m.Variant, m.Line, context, words));
            }
        }

        var ordered = result
            .OrderBy(e => e.FileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Line)
            .ToList();
        return limit > 0 && ordered.Count > limit ? [.. ordered.Take(limit)] : ordered;
    }

    /// <summary>Число различных файлов в выборке.</summary>
    public static int FileCount(IReadOnlyList<ThemeExcerpt> excerpts) =>
        excerpts.Select(e => e.FullPath).Distinct(StringComparer.OrdinalIgnoreCase).Count();
}

using System.Collections.ObjectModel;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>Теги одного файла (преобладающие темы его упоминаний).</summary>
public sealed record FileTags(string FileName, string FullPath, IReadOnlyList<string> Tags);

/// <summary>
/// Группа 3 (item 11): автоматическое тегирование файлов по преобладающим
/// темам упоминаний. Тема каждого упоминания уже посчитана движком
/// (Mention.Theme через ThemeClassifier) — здесь только агрегация.
/// </summary>
public static class FileTagBuilder
{
    public const int MaxTags = 4;

    /// <summary>Тема-кандидат: ≥2 упоминаний и доля ≥5 %; в совсем маленьких файлах (≤2 упоминаний) — любая тема.</summary>
    public const int MinCount = 2;
    public const double MinShareWithCountPercent = 5.0;
    public const int SmallFileMentions = 2;

    public static IReadOnlyList<string> TagsFor(IReadOnlyList<Mention> mentions, int maxTags = MaxTags)
    {
        if (mentions.Count == 0) return [];

        var groups = mentions
            .Where(m => !string.IsNullOrEmpty(m.Theme))
            .GroupBy(m => m.Theme, StringComparer.Ordinal)
            .Select(g => (Theme: g.Key, Count: g.Count()))
            .ToList();
        if (groups.Count == 0) return [];

        var smallFile = mentions.Count <= SmallFileMentions;
        var tags = new List<string>();
        foreach (var (theme, count) in groups
                     .OrderByDescending(g => g.Count)
                     .ThenBy(g => g.Theme, StringComparer.Ordinal))
        {
            if (tags.Count >= Math.Max(1, maxTags)) break;
            var share = count * 100.0 / mentions.Count;
            if ((count >= MinCount && share >= MinShareWithCountPercent) || smallFile)
                tags.Add(theme);
        }
        return tags;
    }

    /// <summary>Теги всех файлов прогона (файлы без тем получают пустой список).</summary>
    public static List<FileTags> Build(AnalysisSession session) =>
        [.. session.Files.Select(f => new FileTags(f.FileName, f.FullPath, TagsFor(f.Mentions)))];

    /// <summary>Объединение тегов корпуса (сортировка по культуре, без дублей).</summary>
    public static List<string> AllTags(IEnumerable<FileTags> files) =>
        [.. files.SelectMany(f => f.Tags)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.CurrentCulture)];
}

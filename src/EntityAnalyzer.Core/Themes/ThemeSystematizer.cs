using EntityAnalyzer.Core.Analysis;

namespace EntityAnalyzer.Core.Themes;

/// <summary>R3-1 (item 53): оценка одного файла по одной теме.</summary>
public sealed record ThemeScore(string Theme, int Hits, IReadOnlyList<string> Words);

/// <summary>R3-1 (item 53): систематизация файла — темы по убыванию вхождений и главная тема.</summary>
public sealed record FileThemes(FileResult File, IReadOnlyList<ThemeScore> Scores, string Dominant)
{
    public string DominantText => Dominant.Length == 0 ? ThemeSystematizer.NoTheme : Dominant;
}

/// <summary>
/// R3-1 (item 53): систематизация текстов по тематическим словам.
/// Слова каждого словаря (включая пользовательские темы вроде «Космологии»)
/// ищутся в контекстах готовой сессии анализа — корпус не пересканируется.
/// Главная тема файла — с максимумом вхождений (при равенстве выигрывает
/// словарь, идущий раньше); файлы без вхождений — «Без темы».
/// </summary>
public static class ThemeSystematizer
{
    /// <summary>Сколько слов-маркеров показывать в строке файла.</summary>
    public const int MaxWordsShown = 6;

    /// <summary>Подпись для файлов, где не нашлось ни одного слова темы.</summary>
    public const string NoTheme = "Без темы";

    /// <summary>Оценки тем для каждого файла сессии (в порядке файлов).</summary>
    public static List<FileThemes> Build(AnalysisSession session, IReadOnlyList<ThemeDictionary> themes)
    {
        var result = new List<FileThemes>(session.Files.Count);
        foreach (var file in session.Files)
        {
            var scores = new List<ThemeScore>();
            foreach (var theme in themes)
            {
                var hits = 0;
                var words = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var mention in file.Mentions)
                {
                    if (string.IsNullOrEmpty(mention.Context)) continue;
                    foreach (var hit in ThemeMatcher.FindHits(mention.Context, theme.Words))
                    {
                        hits++;
                        if (words.Count < MaxWordsShown && seen.Add(hit.Word))
                            words.Add(hit.Word);
                    }
                }
                if (hits > 0) scores.Add(new ThemeScore(theme.Name, hits, words));
            }

            // Сортировка стабильная: при равенстве вхождений сохраняется
            // порядок словарей (встроенные раньше пользовательских).
            var ordered = scores.OrderByDescending(s => s.Hits).ToList();
            result.Add(new FileThemes(file, ordered, ordered.Count > 0 ? ordered[0].Theme : ""));
        }
        return result;
    }

    /// <summary>
    /// Группировка «тема → файлы» по главной теме; «Без темы» — всегда
    /// в конце, остальные темы — по числу файлов, затем по алфавиту.
    /// </summary>
    public static List<(string Theme, List<FileThemes> Files)> Group(IReadOnlyList<FileThemes> all)
    {
        var map = new Dictionary<string, List<FileThemes>>(StringComparer.Ordinal);
        foreach (var file in all)
        {
            var key = file.DominantText;
            if (!map.TryGetValue(key, out var list))
            {
                list = [];
                map[key] = list;
            }
            list.Add(file);
        }
        return map
            .OrderBy(kv => kv.Key == NoTheme ? 1 : 0)
            .ThenByDescending(kv => kv.Value.Count)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
    }
}

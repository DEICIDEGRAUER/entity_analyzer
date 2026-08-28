using System.Collections.ObjectModel;

namespace EntityAnalyzer.Core.Themes;

/// <summary>
/// Keyword-based classifier of mention contexts into narrative themes
/// (port of the original THEMES tables; SBERT semantic scoring was dropped
/// in favour of a fully offline, dependency-free engine).
/// </summary>
public static class ThemeClassifier
{
    private static readonly (string Theme, string[] Keywords)[] Tables =
    [
        ("Мифологическое описание",
        [
            "бог", "боги", "богов", "паук", "живёт", "обитает", "является", "выглядит", "описание",
            "spider", "god", "gods", "living", "appears", "dwells", "form", "body", "head",
            "создание", "существо", "облик", "обитель", "легенда", "миф",
            // QA N08: культово-религиозный пласт — раньше давал «не определена».
            "культ", "культа", "культу", "поклонение", "поклоняются", "почитание",
            "жрец", "жрецы", "жрецом", "жриц", "священник", "пророк", "пророчество",
            "храм", "храма", "храме", "святилище", "алтарь", "идол", "истукан",
            "молитва", "молитв", "молятся", "вера", "веруют", "религия", "секта",
            "cult", "priest", "temple", "worship", "shrine", "idol", "faith"
        ]),
        ("Ритуал и призывание",
        [
            "призыв", "ритуал", "ритуалы", "заклинание", "жертва", "жертвы", "жертвоприношение",
            "алтарь", "summon", "ritual", "sacrifice", "offering", "invoke", "conjure",
            "заклинаю", "вызов", "обряд", "обряды", "церемония", "таинство",
            "круг", "пентаграмма", "печать", "sigil"
        ]),
        ("Художественный сюжет",
        [
            "сказал", "пошёл", "увидел", "ответил", "said", "went", "saw", "replied",
            "heard", "думал", "чувствовал", "stood", "looked", "walked", "cried",
            "рассказ", "повесть", "история"
        ]),
        ("Энциклопедическая статья",
        [
            "название:", "источник:", "http", "copyright", "contents", "see also",
            "references", "статья", "энциклопедия", "определение", "термин",
            "происхождение", "origin", "history"
        ]),
        ("Гримуар",
        [
            "sigil", "gate", "врата", "печать", "заклинаю", "invoke", "conjure",
            "книга", "book", "necronomicon", "манускрипт", "свиток", "parchment",
            "гримуар", "grimoire"
        ])
    ];

    public static IReadOnlyList<string> ThemeNames => Tables.Select(t => t.Theme).ToList();

    /// <summary>Best-matching theme name, or empty string when nothing matches.</summary>
    public static string Classify(string context)
    {
        if (string.IsNullOrWhiteSpace(context)) return "";

        var lower = context.ToLowerInvariant();
        var bestTheme = "";
        var bestScore = 0;

        foreach (var (theme, keywords) in Tables)
        {
            var score = 0;
            foreach (var kw in keywords)
                if (lower.Contains(kw, StringComparison.Ordinal)) score++;

            // Strict substring comparison on the lowered text keeps ordinal Contains safe.
            if (score > bestScore) { bestScore = score; bestTheme = theme; }
        }
        return bestTheme;
    }

    public static Dictionary<string, int> Distribution(IEnumerable<string> contexts)
    {
        var dist = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var ctx in contexts)
        {
            var theme = Classify(ctx);
            if (theme.Length == 0) continue;
            dist[theme] = dist.GetValueOrDefault(theme) + 1;
        }
        return dist;
    }

    /// <summary>Read-only view used by UI to render theme chips.</summary>
    public static ReadOnlyDictionary<string, IReadOnlySet<string>> KeywordIndex { get; } =
        new(Tables.ToDictionary(
            t => t.Theme,
            t => (IReadOnlySet<string>)new HashSet<string>(t.Keywords, StringComparer.Ordinal)));
}

using EntityAnalyzer.Core.Variants;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>
/// Обнаруженный вариант имени (Группа 3, item 8): написание, которое
/// fuzzy-проход нашёл в тексте, но которого нет в каталоге.
/// </summary>
public sealed record DiscoveredVariant(string Entity, string Spelling, int Count, string Example);

/// <summary>
/// Item 8: «обнаруженные варианты имён» — кандидаты на автодополнение
/// каталога. Источник — fuzzy-упоминания (<see cref="Mention.IsFuzzy"/>):
/// их Variant хранит окно текста с маркером «[fuzzy:N%]». Кандидат — это
/// написание без маркера, ещё не входящее ни в один вариант каталога.
/// </summary>
public static class CatalogDiscoverer
{
    public const int DefaultMax = 300;

    public static List<DiscoveredVariant> Discover(
        AnalysisSession session,
        EntityCatalog catalog,
        int max = DefaultMax)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (_, variants) in catalog.VariantsByEntity)
            foreach (var variant in variants)
                known.Add(variant);
        foreach (var def in catalog.Definitions.Values)
        {
            known.Add(def.Name);
            known.Add(def.Canon);
            foreach (var irregular in def.Irregular) known.Add(irregular);
            foreach (var title in def.Titles) known.Add(title);
        }

        const char Sep = '\u0001';
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var display = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var examples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in session.Files)
            foreach (var m in file.Mentions)
            {
                if (!m.IsFuzzy) continue;
                var spelling = StripFuzzyTag(m.Variant);
                if (spelling.Length < 3 || known.Contains(spelling)) continue;
                var key = m.Entity + Sep + spelling;
                counts[key] = counts.GetValueOrDefault(key) + 1;
                if (!display.ContainsKey(key))
                {
                    display[key] = spelling;
                    examples[key] = m.Context ?? "";
                }
            }

        return
        [
            .. counts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Take(Math.Max(1, max))
                .Select(kv => new DiscoveredVariant(
                    kv.Key[..kv.Key.IndexOf(Sep)],
                    display[kv.Key],
                    kv.Value,
                    examples[kv.Key])),
        ];
    }

    /// <summary>«Кулху [fuzzy:87%]» → «Кулху».</summary>
    public static string StripFuzzyTag(string variant)
    {
        var idx = variant.IndexOf(" [fuzzy:", StringComparison.Ordinal);
        return (idx >= 0 ? variant[..idx] : variant).Trim();
    }
}

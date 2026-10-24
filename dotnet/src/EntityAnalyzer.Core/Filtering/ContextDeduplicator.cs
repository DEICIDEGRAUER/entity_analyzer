using EntityAnalyzer.Core.Analysis;

namespace EntityAnalyzer.Core.Filtering;

/// <summary>
/// Session-level content deduplication: identical contexts repeated across
/// dozens of files (скопированные справочники) are grouped into the first
/// occurrence with a repeat counter instead of being printed N times.
/// </summary>
public static class ContextDeduplicator
{
    public static (List<FileResult> Files, int Grouped) Apply(IReadOnlyList<FileResult> files)
    {
        // key → where the surviving mention lives
        var firstLocation = new Dictionary<string, (int File, int Index)>(StringComparer.Ordinal);
        var repeatCounts = new Dictionary<(int File, int Index), int>();
        var keptPerFile = new List<List<Mention>>(files.Count);
        var grouped = 0;

        for (var fi = 0; fi < files.Count; fi++)
        {
            var kept = new List<Mention>(files[fi].Mentions.Count);
            foreach (var m in files[fi].Mentions)
            {
                // Scope the key to the entity: identical context windows can contain
                // several different entities, and collapsing them loses mentions.
                var key = NormalizeKey(m.Entity) + "\u001F" + NormalizeKey(m.Context);
                if (firstLocation.TryGetValue(key, out var original))
                {
                    grouped++;
                    repeatCounts[original] = repeatCounts.TryGetValue(original, out var c) ? c + 1 : 2;
                }
                else
                {
                    firstLocation[key] = (fi, kept.Count);
                    kept.Add(m);
                }
            }
            keptPerFile.Add(kept);
        }

        var result = new FileResult[files.Count];
        for (var fi = 0; fi < files.Count; fi++)
        {
            var file = files[fi];
            var kept = keptPerFile[fi];
            if (kept.Count == file.Mentions.Count && !repeatCounts.Keys.Any(k => k.File == fi))
            {
                result[fi] = file;
                continue;
            }

            var mentions = new List<Mention>(kept.Count);
            for (var i = 0; i < kept.Count; i++)
            {
                var m = kept[i];
                var num = i + 1;
                var rep = repeatCounts.TryGetValue((fi, i), out var r) ? r : m.Repeats;
                mentions.Add(num != m.Num || rep != m.Repeats ? m with { Num = num, Repeats = rep } : m);
            }

            var removed = file.Mentions.Count - kept.Count;
            result[fi] = file with
            {
                Mentions = mentions,
                TotalFound = removed > 0 ? Math.Max(kept.Count, file.TotalFound - removed) : file.TotalFound,
            };
        }

        return (result.ToList(), grouped);
    }

    /// <summary>Case- and whitespace-insensitive content key.</summary>
    public static string NormalizeKey(string context)
    {
        if (string.IsNullOrEmpty(context)) return string.Empty;

        var sb = new System.Text.StringBuilder(context.Length);
        var wasSpace = true;
        foreach (var ch in context)
        {
            if (char.IsWhiteSpace(ch)) { if (!wasSpace) { sb.Append(' '); wasSpace = true; } }
            else { sb.Append(char.ToLowerInvariant(ch)); wasSpace = false; }
        }
        return sb.ToString().Trim();
    }
}

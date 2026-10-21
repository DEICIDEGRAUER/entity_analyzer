namespace EntityAnalyzer.Core.Filtering;

/// <summary>
/// Rejects contexts that are actually encyclopedic listings. v2:
///  • structural evidence (перечисления, буллеты, Title-Case цепочки) — через
///    <see cref="ListingPatternFilter"/> — перевешивает любые «спасения»;
///  • прочие сущности ищутся по границам слов, а не подстрокой
///    («награда» больше не считается упоминанием Нага).
/// </summary>
public sealed class ListingDensityFilter
{
    private readonly IReadOnlySet<string> _mythSignalWords;
    private readonly ListingPatternFilter _patterns;

    public ListingDensityFilter(IReadOnlySet<string> mythSignalWords,
        double maxSegmentRatio = 0.55, int maxSegmentWords = 4)
    {
        _mythSignalWords = mythSignalWords;
        _patterns = new ListingPatternFilter(maxSegmentRatio, maxSegmentWords);
    }

    public bool IsListing(string context, string mentionEntity, IReadOnlyCollection<string> allEntityNames)
    {
        // 1) Структурное перечисление — приговор окончательный.
        if (_patterns.IsEnumeration(context)) return true;

        var ctx = context.ToLowerInvariant();
        var own = mentionEntity.ToLowerInvariant();

        var otherCount = 0;
        foreach (var name in allEntityNames)
        {
            var lower = name.ToLowerInvariant();
            if (lower.Length == 0 || lower == own) continue;
            if (ContainsWholeWord(ctx, lower)) otherCount++;
            if (otherCount >= 4) return true;
        }

        if (otherCount >= 2)
        {
            // Mythological vocabulary rescues the context from the listing verdict.
            foreach (var signal in _mythSignalWords)
                if (ctx.Contains(signal.ToLowerInvariant())) return false;
            return true;
        }

        return false;
    }

    /// <summary>Substring search respecting letter/digit boundaries.</summary>
    internal static bool ContainsWholeWord(string haystack, string needle)
    {
        var start = 0;
        int idx;
        while ((idx = haystack.IndexOf(needle, start, StringComparison.Ordinal)) >= 0)
        {
            var beforeOk = idx == 0 || !IsWordChar(haystack[idx - 1]);
            var afterIdx = idx + needle.Length;
            var afterOk = afterIdx >= haystack.Length || !IsWordChar(haystack[afterIdx]);
            if (beforeOk && afterOk) return true;
            start = idx + 1;
        }
        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c);
}

/// <summary>
/// Entities whose names collide with everyday words («Сон», «Кот», …) are accepted
/// only when mythological vocabulary appears in the surrounding context.
/// </summary>
public sealed class CommonWordContextRule
{
    private readonly IReadOnlySet<string> _mythSignalWords;

    public CommonWordContextRule(IReadOnlySet<string> mythSignalWords)
    {
        _mythSignalWords = mythSignalWords;
    }

    /// <summary>True = mention may pass.</summary>
    public bool Allows(string context, string entity, bool entityIsCommonWord)
    {
        if (!entityIsCommonWord) return true;

        var ctx = context.ToLowerInvariant();
        foreach (var signal in _mythSignalWords)
            if (ctx.Contains(signal.ToLowerInvariant())) return true;
        return false;
    }
}

/// <summary>
/// A title/epithet match («Владыка») is only meaningful when another marker of the
/// same entity reinforces it nearby; otherwise it is an ordinary noun.
/// </summary>
public sealed class TitleValidator
{
    public bool IsValid(string context, string entity, string matchedTitle, IReadOnlyCollection<string> allMarkers)
    {
        _ = entity;
        var ctx = context.ToLowerInvariant();
        var titleLower = matchedTitle.ToLowerInvariant();

        var hasOtherMarker = false;
        foreach (var marker in allMarkers)
        {
            var m = marker.ToLowerInvariant();
            if (m.Length == 0 || m == titleLower) continue;
            hasOtherMarker = true;
            if (ctx.Contains(m)) return true;
        }

        return !hasOtherMarker;
    }
}

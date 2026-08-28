namespace EntityAnalyzer.Core.Filtering;

/// <summary>
/// Structural enumeration detector. Fires on:
///  • comma/semicolon/bullet/newline name lists (segment analysis);
///  • runs of bullet markers;
///  • long Title-Case chains almost free of function words
///    («Великие Древние Азатот Флейтист Танцор …» — справочники и оглавления).
/// </summary>
public sealed class ListingPatternFilter
{
    private readonly double _maxSegmentRatio;
    private readonly int _maxSegmentWords;
    private const int MinSegments = 3;
    private const int BulletRunThreshold = 3;
    private const int ChainMinWords = 14;

    public ListingPatternFilter(double maxSegmentRatio = 0.55, int maxSegmentWords = 4)
    {
        _maxSegmentRatio = maxSegmentRatio;
        _maxSegmentWords = maxSegmentWords;
    }

    public bool IsEnumeration(string context)
    {
        if (string.IsNullOrWhiteSpace(context)) return false;

        if (HasBulletRun(context)) return true;
        if (HasNameSegments(context)) return true;
        if (HasTitleCaseChain(context)) return true;

        return false;
    }

    // ── D1: сегменты между , ; • · | и переводами строк ──

    private bool HasNameSegments(string context)
    {
        var segments = SplitSegments(context);
        if (segments.Count < MinSegments) return false;

        var shortCount = 0;
        var capitalized = 0;
        foreach (var seg in segments)
        {
            if (CountWords(seg) <= _maxSegmentWords) shortCount++;
            if (StartsWithUpper(seg)) capitalized++;
        }

        var shortRatio = (double)shortCount / segments.Count;
        var capRatio = (double)capitalized / segments.Count;

        return shortRatio >= _maxSegmentRatio && capRatio >= 0.5;
    }

    private static List<string> SplitSegments(string context)
    {
        var list = new List<string>(16);
        var start = 0;
        for (var i = 0; i <= context.Length; i++)
        {
            var isSep = i == context.Length || context[i] is ',' or ';' or '\u2022' or '\u00B7' or '|' or '\n';
            if (!isSep) continue;
            var seg = context[start..i].Trim(' ', '-', '\u2013', '\u2014', ')', '(', '.', '"');
            if (seg.Length > 0) list.Add(seg);
            start = i + 1;
        }
        return list;
    }

    // ── D2: серия буллетов ──

    private static bool HasBulletRun(string context)
    {
        var count = 0;
        foreach (var c in context)
        {
            if (c is '\u2022' or '\u00B7' or '\u25CF' or '\u25AA')
            {
                count++;
                if (count >= BulletRunThreshold) return true;
            }
        }
        return false;
    }

    // ── D3: длинная цепочка слов с заглавной почти без служебных ──

    private bool HasTitleCaseChain(string context)
    {
        var words = Tokenize(context);
        if (words.Count < ChainMinWords) return false;

        var caps = 0;
        var function = 0;
        foreach (var w in words)
        {
            if (char.IsUpper(w[0])) caps++;
            if (FunctionWords.Contains(w)) function++;
        }

        var capRatio = (double)caps / words.Count;
        var funcRatio = (double)function / words.Count;

        return capRatio >= 0.55 && funcRatio <= 0.20;
    }

    internal static bool StartsWithUpper(string s) =>
        s.Length > 0 && char.IsUpper(s[0]);

    internal static int CountWords(string s)
    {
        var count = 0;
        var inWord = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) inWord = false;
            else if (!inWord) { inWord = true; count++; }
        }
        return count;
    }

    private static List<string> Tokenize(string s)
    {
        var list = new List<string>(32);
        var start = -1;
        for (var i = 0; i <= s.Length; i++)
        {
            var letter = i < s.Length && char.IsLetter(s[i]);
            if (letter && start < 0) start = i;
            else if (!letter && start >= 0) { list.Add(s[start..i]); start = -1; }
        }
        return list;
    }

    /// <summary>Служебные слова русского и английского (нижний регистр).</summary>
    internal static readonly HashSet<string> FunctionWords = new(StringComparer.Ordinal)
    {
        // русские
        "и","в","во","не","на","я","быть","он","с","со","а","то","как","что","по","это","она",
        "этот","к","до","у","вы","за","бы","ее","его","от","о","из","им","там","для","уже","если",
        "или","ни","был","была","были","было","при","том","чтобы","же","все","тот","эта","так",
        "его","её","их","но","да","ну","ли","ведь","вот","под","над","об","без","через","между",
        "английские",
        "the","of","and","in","to","was","is","he","she","it","they","that","with","as","at",
        "by","for","on","from","but","or","not","his","her","its","their","be","been","were",
        "had","has","have","which","who","whom","this","these","those","there","then","than",
        "so","if","when","an","a","are","am","do","does","did","will","would","could","shall",
        "should","may","might","must","can","upon","into","over","under","about","after","before",
    };
}

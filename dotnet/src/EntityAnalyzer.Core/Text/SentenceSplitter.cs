using System.Buffers;

namespace EntityAnalyzer.Core.Text;

/// <summary>A sentence located in the original text. <see cref="End"/> is exclusive.
/// <see cref="WordCount"/> is precomputed during the scan — no per-sentence string
/// materialization for the whole corpus.</summary>
public readonly record struct SentenceSpan(int Start, int End, int WordCount);

/// <summary>
/// Splits text into sentences while keeping exact offsets into the original string.
/// Improvements over the original Python splitter:
///  • works on the raw text (no pre-collapsing), so coordinates are trustworthy;
///  • does not split decimal numbers like "3.14";
///  • keeps every fragment (the Python version silently dropped short ones).
/// </summary>
public static class SentenceSplitter
{
    private const char Ellipsis = '\u2026';

    private static bool IsTerminator(char c) => c is '.' or '!' or '?' || c == Ellipsis;
    private static bool IsClosingPunct(char c) =>
        c is '"' or '\'' or ')' or ']' or '\u00BB' /* » */ or '\u201D' or '\u2019';

    public static List<string> Split(string text) =>
        SplitWithSpans(text).Select(s => text.Substring(s.Start, s.End - s.Start)).ToList();

    public static List<SentenceSpan> SplitWithSpans(string text)
    {
        var result = new List<SentenceSpan>();
        if (string.IsNullOrWhiteSpace(text)) return result;

        var n = text.Length;
        var segStart = 0;

        void AddIfMeaningful(int start, int end)
        {
            if (end <= start) return;
            while (start < end && char.IsWhiteSpace(text[start])) start++;
            while (end > start && char.IsWhiteSpace(text[end - 1])) end--;
            if (end <= start) return;

            var words = 0;
            var inWord = false;
            for (var i = start; i < end; i++)
            {
                if (char.IsWhiteSpace(text[i])) inWord = false;
                else if (!inWord) { inWord = true; words++; }
            }
            result.Add(new SentenceSpan(start, end, words));
        }

        for (var i = 0; i < n; i++)
        {
            var c = text[i];
            if (!IsTerminator(c)) continue;

            // Decimal guard: "3.14" — a dot squeezed between digits is not a terminator.
            if (c == '.' && i > segStart && char.IsDigit(text[i - 1]) && i + 1 < n && char.IsDigit(text[i + 1]))
                continue;

            // Consume runs of terminators ("!!!", "?!", "….").
            var j = i + 1;
            while (j < n && IsTerminator(text[j])) j++;

            // Attach immediately following closing quotes/brackets.
            while (j < n && IsClosingPunct(text[j]) && !char.IsWhiteSpace(text[j - 1]))
                j++;

            // Dialogue-dash guard: «Стой!» — сказал он. — the dash clause belongs
            // to the previous utterance, so do not start a new sentence here.
            var k = j;
            while (k < n && char.IsWhiteSpace(text[k])) k++;
            var isDialogueDash = k < n
                && (text[k] == '\u2014' || text[k] == '\u2013' || text[k] == '-')
                && (k + 1 >= n || text[k + 1] == ' ');

            if (!isDialogueDash)
            {
                AddIfMeaningful(segStart, j);
                segStart = j;
            }
            i = Math.Max(j, k) - 1;
        }

        AddIfMeaningful(segStart, n);
        return result;
    }

    internal static int CountWords(string s)
    {
        var count = 0;
        var inWord = false;
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) { inWord = false; }
            else if (!inWord) { inWord = true; count++; }
        }
        return count;
    }
}

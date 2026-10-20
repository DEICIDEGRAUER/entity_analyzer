using System.Text.RegularExpressions;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>Совпавший фрагмент между двумя файлами (Группа 3, item 29).</summary>
public sealed record IntertextMatch(
    string Kind, string FragmentA, string FragmentB, int LineA, int LineB);

/// <summary>Пара файлов с общими фрагментами.</summary>
public sealed record IntertextPair(
    string FileA,
    string PathA,
    string FileB,
    string PathB,
    int SharedCount,
    IReadOnlyList<IntertextMatch> Matches);

/// <summary>
/// Item 29: интертекстуальные связи между файлами. «Дословно» — общие
/// 4-граммы слов, «близко» — общие 3-граммы вне дословных совпадений
/// (фраза отличается одним словом). Слова сравниваются без учёта регистра,
/// фрагменты выводятся в исходном написании. n-граммы, встречающиеся более
/// чем в maxFilesPerShingle файлах, отбрасываются как общий оборот.
/// Файлы не читаются — тексты передаёт вызывающая сторона.
/// </summary>
public static partial class IntertextFinder
{
    public const int ExactWords = 4;
    public const int NearWords = 3;
    public const int MaxFilesPerShingle = 8;
    public const int DefaultMaxPairs = 200;
    public const int DefaultMaxMatchesPerPair = 10;

    public static List<IntertextPair> Find(
        IReadOnlyList<CorpusText> files,
        int maxPairs = DefaultMaxPairs,
        int maxMatchesPerPair = DefaultMaxMatchesPerPair,
        int maxFilesPerShingle = MaxFilesPerShingle)
    {
        var texts = new List<FileWords>();
        foreach (var file in files)
        {
            if (string.IsNullOrWhiteSpace(file.Text)) continue;
            var words = Tokenize(file.Text);
            if (words.Count >= NearWords)
                texts.Add(new FileWords(file, words, FragmentSearcher.LineStarts(file.Text)));
        }
        if (texts.Count < 2) return [];

        var pairs = new Dictionary<(int A, int B), PairAccum>();
        foreach (var (_, occurrences) in BuildShingles(texts, ExactWords, maxFilesPerShingle))
            Accumulate(pairs, occurrences, isExact: true);
        foreach (var (_, occurrences) in BuildShingles(texts, NearWords, maxFilesPerShingle))
            Accumulate(pairs, occurrences, isExact: false);

        var result = new List<IntertextPair>();
        foreach (var ((aIdx, bIdx), acc) in pairs)
        {
            var exactRanges = acc.Exact;
            var matches = new List<IntertextMatch>();

            foreach (var (posA, posB) in acc.Exact.OrderBy(m => m.PosA))
                matches.Add(new IntertextMatch(
                    "дословно",
                    Fragment(texts[aIdx], posA, ExactWords),
                    Fragment(texts[bIdx], posB, ExactWords),
                    LineOf(texts[aIdx], posA),
                    LineOf(texts[bIdx], posB)));

            foreach (var (posA, posB) in acc.Near.OrderBy(m => m.PosA))
            {
                if (OverlapsExact(exactRanges, posA, posB)) continue;
                matches.Add(new IntertextMatch(
                    "близко",
                    Fragment(texts[aIdx], posA, NearWords),
                    Fragment(texts[bIdx], posB, NearWords),
                    LineOf(texts[aIdx], posA),
                    LineOf(texts[bIdx], posB)));
            }

            if (matches.Count == 0) continue;
            result.Add(new IntertextPair(
                texts[aIdx].Corpus.File, texts[aIdx].Corpus.Path,
                texts[bIdx].Corpus.File, texts[bIdx].Corpus.Path,
                matches.Count,
                [.. matches.Take(maxMatchesPerPair)]));
        }

        return
        [
            .. result
                .OrderByDescending(p => p.SharedCount)
                .ThenBy(p => p.FileA, StringComparer.Ordinal)
                .ThenBy(p => p.FileB, StringComparer.Ordinal)
                .Take(Math.Max(1, maxPairs)),
        ];
    }

    /// <summary>n-граммы: ключ → вхождения (не более одного на файл).</summary>
    private static Dictionary<string, List<(int FileIdx, int Pos)>> BuildShingles(
        List<FileWords> files, int size, int maxFilesPerShingle)
    {
        var map = new Dictionary<string, List<(int FileIdx, int Pos)>>(StringComparer.Ordinal);
        for (var fileIdx = 0; fileIdx < files.Count; fileIdx++)
        {
            var words = files[fileIdx].Words;
            for (var pos = 0; pos + size <= words.Count; pos++)
            {
                var key = string.Join('\u0001', words.Skip(pos).Take(size).Select(w => w.Lower));
                if (!map.TryGetValue(key, out var list))
                    map[key] = list = [];
                if (list.Count > 0 && list[^1].FileIdx == fileIdx) continue;
                list.Add((fileIdx, pos));
            }
        }
        return map
            .Where(kv => kv.Value.Count >= 2 && kv.Value.Count <= maxFilesPerShingle)
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }

    private static void Accumulate(
        Dictionary<(int A, int B), PairAccum> pairs,
        List<(int FileIdx, int Pos)> occurrences,
        bool isExact)
    {
        for (var i = 0; i < occurrences.Count; i++)
            for (var j = i + 1; j < occurrences.Count; j++)
            {
                var key = (occurrences[i].FileIdx, occurrences[j].FileIdx);
                if (!pairs.TryGetValue(key, out var acc))
                    pairs[key] = acc = new PairAccum();
                var match = (occurrences[i].Pos, occurrences[j].Pos);
                if (isExact) acc.Exact.Add(match);
                else acc.Near.Add(match);
            }
    }

    /// <summary>Близкое совпадение не повторяет дословное (пересекаются оба диапазона).</summary>
    private static bool OverlapsExact(
        List<(int PosA, int PosB)> exactRanges, int posA, int posB)
    {
        foreach (var (exactA, exactB) in exactRanges)
        {
            var overlapA = Math.Max(posA, exactA) <= Math.Min(posA + NearWords - 1, exactA + ExactWords - 1);
            if (!overlapA) continue;
            var overlapB = Math.Max(posB, exactB) <= Math.Min(posB + NearWords - 1, exactB + ExactWords - 1);
            if (overlapB) return true;
        }
        return false;
    }

    private static string Fragment(FileWords file, int pos, int count)
    {
        var start = file.Words[pos].Start;
        var end = file.Words[pos + count - 1].End;
        return file.Corpus.Text[start..end];
    }

    private static int LineOf(FileWords file, int pos) =>
        FragmentSearcher.LineOf(file.LineStarts, file.Words[pos].Start);

    private static List<WordTok> Tokenize(string text) =>
        [.. WordRegex().Matches(text)
            .Select(m => new WordTok(m.Value.ToLowerInvariant(), m.Index, m.Index + m.Length))];

    private readonly record struct WordTok(string Lower, int Start, int End);

    private sealed class FileWords(CorpusText corpus, List<WordTok> words, List<int> lineStarts)
    {
        public CorpusText Corpus { get; } = corpus;
        public List<WordTok> Words { get; } = words;
        public List<int> LineStarts { get; } = lineStarts;
    }

    private sealed class PairAccum
    {
        public List<(int PosA, int PosB)> Exact { get; } = [];
        public List<(int PosA, int PosB)> Near { get; } = [];
    }

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:[-'’][\p{L}\p{N}]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}

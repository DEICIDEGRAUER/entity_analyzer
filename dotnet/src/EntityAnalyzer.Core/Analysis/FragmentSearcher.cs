using System.Text.RegularExpressions;
using EntityAnalyzer.Core.Fuzzy;
using EntityAnalyzer.Core.Variants;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>
/// Файл корпуса для поиска по фрагменту: отображаемое имя, путь и
/// нормализованный текст (уже прочитанный вызывающей стороной).
/// </summary>
public sealed record CorpusText(string File, string Path, string Text);

/// <summary>
/// Найденный участок текста (Группа 3, item 31): файл, строка, позиция,
/// фрагмент с окрестностью и вид совпадения.
/// </summary>
public sealed record FragmentHit(
    string File,
    string Path,
    int Line,
    int CharIndex,
    string Fragment,
    int Score,
    string Kind);

/// <summary>
/// Item 31: поиск по фрагменту по всем файлам корпуса в три уровня.
/// 1) «точное» — подстрока без учёта регистра (по границам слов);
/// 2) «морфология» — слова фразы в любой падежной форме (RussianMorphology)
///    плюс переданные синонимы (варианты имён из каталога) — тоже с падежными
///    формами: «Ктулху» найдёт и альтернативное написание той же сущности;
/// 3) «приблизительное» (fuzzy/парафраз) — та же фраза с искажениями написания
/// слов: ограниченное расстояние Левенштейна с порогом, как в fuzzy-проходе.
/// Слабые уровни не повторяют уже найденные позиции. Поиск детерминирован,
/// без чтения файлов — тексты передаются вызывающей стороной.
/// </summary>
public static partial class FragmentSearcher
{
    public const int DefaultMaxPerFile = 10;
    public const int DefaultMaxTotal = 300;

    /// <summary>Порог схожести слова с искомым — как у fuzzy-прохода (R1-11).</summary>
    public const int FuzzyThreshold = 85;

    private const int SnippetRadius = 48;
    private const int SnippetMax = 180;

    public static List<FragmentHit> Search(
        string query,
        IReadOnlyList<CorpusText> files,
        bool morphology = true,
        bool fuzzy = true,
        int maxPerFile = DefaultMaxPerFile,
        int maxTotal = DefaultMaxTotal,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? synonyms = null)
    {
        var hits = new List<FragmentHit>();
        var tokens = Tokenize(query);
        if (tokens.Count == 0 || files.Count == 0) return hits;

        var phrase = Normalize(query);
        foreach (var file in files)
        {
            if (hits.Count >= maxTotal) break;
            if (string.IsNullOrWhiteSpace(file.Text)) continue;
            var fileHits = FindInFile(file, phrase, tokens, morphology, fuzzy, maxPerFile, synonyms);
            foreach (var hit in fileHits)
            {
                hits.Add(hit);
                if (hits.Count >= maxTotal) break;
            }
        }
        return hits;
    }

    private static List<FragmentHit> FindInFile(
        CorpusText file, string phrase, List<string> tokens,
        bool morphology, bool fuzzy, int maxPerFile,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? synonyms)
    {
        var text = file.Text;
        var words = WordRegex().Matches(text);
        var lineStarts = LineStarts(text);
        var claimed = new List<(int Start, int End)>();
        var results = new List<FragmentHit>();

        // Уровень 1: точная подстрока (без учёта регистра, по границам слов).
        var from = 0;
        while (results.Count < maxPerFile)
        {
            var at = text.IndexOf(phrase, from, StringComparison.OrdinalIgnoreCase);
            if (at < 0) break;
            var end = at + phrase.Length;
            from = at + 1;
            if (!HasWordBoundaries(text, at, end, phrase)) continue;
            claimed.Add((at, end));
            results.Add(MakeHit(file, text, lineStarts, at, end, 100, "точное"));
        }

        if (results.Count >= maxPerFile || words.Count < tokens.Count) return results;

        // Уровни 2 и 3: скользящее окно из tokens.Count слов.
        var lowers = new string[words.Count];
        for (var i = 0; i < words.Count; i++)
            lowers[i] = words[i].Value.ToLowerInvariant();
        var accepted = new HashSet<string>[tokens.Count];
        for (var k = 0; k < tokens.Count; k++)
            accepted[k] = AcceptedForms(tokens[k], morphology, synonyms);

        for (var i = 0; i + tokens.Count <= words.Count && results.Count < maxPerFile; i++)
        {
            var start = words[i].Index;
            var last = words[i + tokens.Count - 1];
            var end = last.Index + last.Length;
            if (Overlaps(claimed, start, end)) continue;

            var morphOk = true;
            for (var k = 0; k < tokens.Count; k++)
                if (!accepted[k].Contains(lowers[i + k])) { morphOk = false; break; }

            if (morphOk)
            {
                claimed.Add((start, end));
                results.Add(MakeHit(file, text, lineStarts, start, end, 100, "морфология"));
                continue;
            }

            if (!fuzzy) continue;
            var worst = 100;
            for (var k = 0; k < tokens.Count; k++)
            {
                var pairScore = PairScore(lowers[i + k], tokens[k]);
                if (pairScore < FuzzyThreshold) { worst = -1; break; }
                if (pairScore < worst) worst = pairScore;
            }
            if (worst < 0) continue;

            claimed.Add((start, end));
            results.Add(MakeHit(file, text, lineStarts, start, end, worst, "приблизительное"));
        }

        results.Sort((a, b) => a.CharIndex.CompareTo(b.CharIndex));
        return results;
    }

    /// <summary>
    /// Формы слова фразы: сам токен + падежные формы (если включена морфология)
    /// + синонимы каталога — другие написания той же сущности и их падежные
    /// формы. Синонимы подставляются и когда сам токен — падежная форма варианта.
    /// </summary>
    private static HashSet<string> AcceptedForms(
        string token, bool morphology, IReadOnlyDictionary<string, IReadOnlyList<string>>? synonyms)
    {
        var set = new HashSet<string>(StringComparer.Ordinal) { token };
        var bases = new List<string> { token };
        if (morphology)
            bases.AddRange(RussianMorphology.Decline(token).Select(f => f.ToLowerInvariant()));

        if (synonyms is not null)
            foreach (var basis in bases)
            {
                if (!synonyms.TryGetValue(basis, out var siblings)) continue;
                foreach (var sibling in siblings)
                {
                    var form = sibling?.Trim().ToLowerInvariant();
                    if (string.IsNullOrEmpty(form)) continue;
                    set.Add(form);
                    if (!morphology) continue;
                    foreach (var declined in RussianMorphology.Decline(form))
                        set.Add(declined.ToLowerInvariant());
                }
            }

        foreach (var basis in bases) set.Add(basis);
        return set;
    }

    /// <summary>Схожесть слова с токеном в процентах; -1 — ниже порога.</summary>
    private static int PairScore(string word, string token)
    {
        if (string.Equals(word, token, StringComparison.Ordinal)) return 100;
        var maxLen = Math.Max(word.Length, token.Length);
        var allowed = Math.Max(0, (int)Math.Floor(maxLen * (1 - FuzzyThreshold / 100.0)));
        var distance = Levenshtein.Bounded(word, token, allowed + 1);
        if (distance > allowed) return -1;
        return (int)Math.Round((1 - distance / (double)maxLen) * 100);
    }

    private static bool HasWordBoundaries(string text, int start, int end, string phrase)
    {
        if (phrase.Length == 0) return false;
        if (IsWordChar(phrase[0]) && start > 0 && IsWordChar(text[start - 1])) return false;
        if (IsWordChar(phrase[^1]) && end < text.Length && IsWordChar(text[end])) return false;
        return true;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c);

    private static bool Overlaps(List<(int Start, int End)> claimed, int start, int end)
    {
        foreach (var (otherStart, otherEnd) in claimed)
            if (start < otherEnd && otherStart < end)
                return true;
        return false;
    }

    private static FragmentHit MakeHit(
        CorpusText file, string text, List<int> lineStarts,
        int start, int end, int score, string kind) =>
        new(file.File, file.Path, LineOf(lineStarts, start), start,
            Snippet(text, start, end), score, kind);

    internal static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n')
                starts.Add(i + 1);
        return starts;
    }

    internal static int LineOf(List<int> starts, int index)
    {
        var lo = 0;
        var hi = starts.Count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (starts[mid] <= index) lo = mid;
            else hi = mid - 1;
        }
        return lo + 1;
    }

    /// <summary>Окрестность совпадения по границам слов, с многоточиями по краям.</summary>
    internal static string Snippet(string text, int start, int end)
    {
        var from = Math.Max(0, start - SnippetRadius);
        var to = Math.Min(text.Length, end + SnippetRadius);

        if (from > 0)
        {
            var space = text.IndexOf(' ', from);
            if (space >= 0 && space < start) from = space + 1;
        }
        if (to < text.Length)
        {
            var space = text.LastIndexOf(' ', to);
            if (space > end) to = space;
        }

        var fragment = text[from..to].Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (fragment.Length > SnippetMax)
            fragment = fragment[..SnippetMax].TrimEnd() + "…";
        var prefix = from > 0 ? "…" : "";
        var suffix = to < text.Length ? "…" : "";
        return prefix + fragment + suffix;
    }

    private static List<string> Tokenize(string query) =>
        [.. WordRegex().Matches(query ?? "")
            .Select(m => m.Value.ToLowerInvariant())];

    private static string Normalize(string query) =>
        WhitespaceRegex().Replace((query ?? "").Trim(), " ");

    [GeneratedRegex(@"[\p{L}\p{N}]+(?:[-'’][\p{L}\p{N}]+)*", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}

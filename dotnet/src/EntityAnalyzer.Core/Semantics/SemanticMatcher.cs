using System.Text.RegularExpressions;
using EntityAnalyzer.Core.Analysis;

namespace EntityAnalyzer.Core.Semantics;

/// <summary>
/// Сканер семантических паттернов: для каждого предложения проверяет,
/// встречается ли заданная паттерном последовательность грамматических
/// классов. Поддерживаются: словари, имена собственные, литералы, regex,
/// альтернативы (AnyOf), опциональные токены (Required=false), негативные
/// стражи (Not — слово перед следующим токеном не должно соответствовать)
/// и общее окно TotalWindow на весь паттерн.
/// </summary>
public sealed partial class SemanticMatcher
{
    private readonly IReadOnlyList<SemanticPattern> _patterns;
    private readonly LexiconSet _lexicons;
    private readonly HashSet<string> _knownNames;
    private readonly int _maxPerPatternPerFile;

    // R1-4: якорная компиляция паттернов. Якорь — обязательный литерал
    // (или any_of из литералов) среди первых потребляющих токенов:
    // предложение без слов-якорей не может соответствовать паттерну,
    // поэтому перебор стартовых позиций для него не запускается.
    private readonly bool _useAnchors;
    private readonly Dictionary<string, List<int>> _anchorIndex = new(StringComparer.Ordinal);
    private readonly List<int> _legacyPatterns = [];

    [GeneratedRegex(@"[\p{L}\p{N}]+", RegexOptions.Compiled)]
    private static partial Regex WordRegex();

    public SemanticMatcher(IReadOnlyList<SemanticPattern> patterns, LexiconSet lexicons,
        IEnumerable<string> knownEntityNames, int maxPerPatternPerFile = 50,
        bool useAnchoring = true)
    {
        // Паттерны с некорректными Not-хвостами (страж в конце) отбрасываются:
        // стражу нужен последующий потребляющий токен.
        _patterns = [.. patterns.Where(p => p.Enabled && p.Tokens.Count > 0 && IsValid(p))];
        _lexicons = lexicons;
        _knownNames = new HashSet<string>(knownEntityNames, StringComparer.OrdinalIgnoreCase);
        _maxPerPatternPerFile = Math.Max(1, maxPerPatternPerFile);
        _useAnchors = useAnchoring;
        CompileAnchors();
    }

    /// <summary>Диагностика классификации: паттерн использует якорный индекс?</summary>
    internal bool IsAnchored(string patternId)
    {
        for (var i = 0; i < _patterns.Count; i++)
            if (string.Equals(_patterns[i].Id, patternId, StringComparison.OrdinalIgnoreCase))
                return !_legacyPatterns.Contains(i);
        return false;
    }

    /// <summary>R1-4: построение якорного индекса по первым потребляющим токенам.</summary>
    private void CompileAnchors()
    {
        for (var i = 0; i < _patterns.Count; i++)
        {
            var p = _patterns[i];
            var ti = 0;
            while (ti < p.Tokens.Count && p.Tokens[ti].Kind == TokenKind.Not) ti++;
            if (ti >= p.Tokens.Count) { _legacyPatterns.Add(i); continue; }
            var t = p.Tokens[ti];
            // Якорить можно только токен, который обязан быть потреблён ровно один раз:
            // опциональный/повторяемый первый токен дал бы ложный негатив.
            if (!t.Required || t.Min < 1 || t.Max > 1) { _legacyPatterns.Add(i); continue; }

            if (t.Kind == TokenKind.Literal)
            {
                AddAnchor(Normalize(t.Value), i);
            }
            else if (t.Kind == TokenKind.AnyOf &&
                     t.Options is { Count: > 0 } &&
                     t.Options.All(o => o.Kind == TokenKind.Literal))
            {
                foreach (var o in t.Options) AddAnchor(Normalize(o.Value), i);
            }
            else
            {
                _legacyPatterns.Add(i);
            }
        }
    }

    private void AddAnchor(string word, int patternIdx)
    {
        if (!_anchorIndex.TryGetValue(word, out var list))
            _anchorIndex[word] = list = [];
        list.Add(patternIdx);
    }

    public bool HasPatterns => _patterns.Count > 0;

    /// <summary>Скан одного нормализованного текста.</summary>
    public List<SemanticMatch> Scan(string normalized, string filePath, int[] lineStarts)
    {
        var results = new List<SemanticMatch>();
        if (!HasPatterns) return results;

        var sentences = Text.SentenceSplitter.SplitWithSpans(normalized);
        var perPattern = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var sentence in sentences)
        {
            var fragment = normalized[sentence.Start..sentence.End];
            var words = new List<(string Text, int Offset)>();
            foreach (Match m in WordRegex().Matches(fragment))
                words.Add((m.Value, m.Index));

            if (words.Count == 0) continue;

            // R1-4: кандидаты — только паттерны с якорем в этом предложении
            // (+ легаси-паттерны без якоря); порядок детерминированный.
            List<int> candidates;
            if (_useAnchors)
            {
                candidates = new List<int>(_legacyPatterns);
                foreach (var w in words)
                {
                    var key = Normalize(w.Text);
                    if (_anchorIndex.TryGetValue(key, out var hits))
                        foreach (var pi in hits) if (!candidates.Contains(pi)) candidates.Add(pi);
                }
                candidates.Sort();
            }
            else
            {
                candidates = Enumerable.Range(0, _patterns.Count).ToList();
            }

            foreach (var pi in candidates)
            {
                var pattern = _patterns[pi];
                if (perPattern.TryGetValue(pattern.Id, out var count) &&
                    count >= _maxPerPatternPerFile)
                    continue;

                if (TryMatchSequence(pattern, words, out var startWord, out var endWord))
                {
                    var snippetStart = words[startWord].Offset;
                    var snippetEnd = Math.Min(fragment.Length,
                        words[endWord].Offset + words[endWord].Text.Length + 40);
                    var snippet = fragment[snippetStart..snippetEnd].Trim();
                    if (snippet.Length > 200) snippet = snippet[..200].Trim() + "…";

                    var absolutePos = sentence.Start + snippetStart;
                    var line = CountLine(lineStarts, absolutePos);

                    results.Add(new SemanticMatch(
                        pattern.Id, pattern.Name, filePath, line, snippet, absolutePos));

                    perPattern[pattern.Id] = count + 1;
                }
            }
        }

        return results;
    }

    /// <summary>
    /// Поиск последовательности: рекурсия с бэктрекингом обрабатывает
    /// опциональные токены, альтернативы и стражи Not; общее окно
    /// TotalWindow проверяется в момент завершения цепочки.
    /// </summary>
    internal bool TryMatchSequence(SemanticPattern pattern,
        List<(string Text, int Offset)> words, out int startIdx, out int endIdx)
    {
        if (MatchFrom(pattern, 0, words, -1, -1, out startIdx, out endIdx))
            return true;
        startIdx = endIdx = -1;
        return false;
    }

    /// <summary>
    /// Сопоставление токенов начиная с ti; cursor — индекс последнего
    /// потреблённого слова (-1 — ничего); firstIdx — индекс первого слова
    /// цепочки (для проверки TotalWindow).
    /// </summary>
    private bool MatchFrom(SemanticPattern p, int ti,
        List<(string Text, int Offset)> words, int cursor, int firstIdxIn,
        out int firstIdx, out int endIdx)
    {
        firstIdx = firstIdxIn;
        endIdx = -1;

        if (ti >= p.Tokens.Count)
        {
            // Общее окно: размах от первого до последнего токена в словах.
            if (p.TotalWindow is { } tw && firstIdx >= 0 && cursor - firstIdx > tw)
                return false; // слишком широко — бэктрекинг ищет компактнее
            endIdx = cursor;
            return true;
        }

        // Стражи Not копятся до ближайшего потребляющего токена.
        var guards = new List<SemanticToken>();
        while (p.Tokens[ti].Kind == TokenKind.Not)
        {
            var notToken = p.Tokens[ti]; // локальная переменная — для null-анализа
            if (notToken.Options is { Count: > 0 })
                guards.AddRange(notToken.Options);
            ti++;
            if (ti >= p.Tokens.Count) return false; // страж без цели — недопустим
        }
        var token = p.Tokens[ti];

        // Повторы токена подряд (Min..Max): пробуем от максимума к минимуму —
        // бэктрекинг найдёт допустимую длину. Опциональный токен (Required=false)
        // получает Min=0: ветка «без него» обрабатывается нулевым k.
        var min = token.Required ? Math.Max(1, token.Min) : Math.Min(token.Min, 0);
        var max = Math.Max(min, token.Max);

        // Нулевой k (только для опциональных): пропустить токен целиком.
        if (min == 0 &&
            MatchFrom(p, ti + 1, words, cursor, firstIdxIn, out firstIdx, out endIdx))
            return true;

        if (cursor < 0)
        {
            // Первый потребляющий токен паттерна: перебор стартовых позиций
            // по всему предложению (окно не ограничивает первый токен).
            for (var s = 0; s < words.Count; s++)
            {
                for (var k = Math.Min(max, words.Count - s); k >= Math.Max(1, min); k--)
                {
                    if (!TryConsumeK(token, words, s, k)) continue;

                    // Стражи: слово непосредственно ПЕРЕД первым повтором.
                    if (guards.Count > 0 && s > 0)
                    {
                        var violated = false;
                        foreach (var g in guards)
                            if (MatchesToken(g, words, s - 1)) { violated = true; break; }
                        if (violated) continue;
                    }

                    if (MatchFrom(p, ti + 1, words, s + k - 1, s, out firstIdx, out endIdx))
                        return true;
                }
            }
        }
        else
        {
            // Последующие токены: старт первого повтора — в пределах окна
            // после предыдущего слова (разрыв ≤ Window), затем k слов подряд.
            var lo = cursor + 1;
            var hi = Math.Min(words.Count - 1, cursor + p.Window + 1);
            for (var s = lo; s <= hi; s++)
            {
                var limitK = Math.Min(max, words.Count - s);
                for (var k = limitK; k >= Math.Max(1, min); k--)
                {
                    if (!TryConsumeK(token, words, s, k)) continue;

                    if (guards.Count > 0 && s > 0)
                    {
                        var violated = false;
                        foreach (var g in guards)
                            if (MatchesToken(g, words, s - 1)) { violated = true; break; }
                        if (violated) continue;
                    }

                    if (MatchFrom(p, ti + 1, words, s + k - 1, firstIdxIn, out firstIdx, out endIdx))
                        return true;
                }
            }
        }

        firstIdx = firstIdxIn;
        endIdx = -1;
        return false;
    }

    /// <summary>Потребить k подряд идущих слов, соответствующих токену, начиная с позиции start.</summary>
    private bool TryConsumeK(SemanticToken token, List<(string Text, int Offset)> words,
        int start, int k)
    {
        for (var n = 0; n < k; n++)
        {
            if (start + n >= words.Count) return false;
            if (!MatchesToken(token, words, start + n)) return false;
        }
        return true;
    }

    private bool MatchesToken(SemanticToken token, List<(string Text, int Offset)> words, int index)
    {
        var word = words[index].Text;
        var lower = word.ToLowerInvariant();

        return token.Kind switch
        {
            TokenKind.Literal => string.Equals(lower, Normalize(token.Value), StringComparison.Ordinal),
            TokenKind.Lexicon => _lexicons.Contains(token.Value, word),
            TokenKind.Regex => SafeIsMatch(token.Value, word),
            TokenKind.AnyWord => true,
            TokenKind.Proper => IsProper(word, index),
            TokenKind.Capitalized => IsCapitalized(word),
            TokenKind.AnyOf => token.Options is { Count: > 0 } &&
                               token.Options.Any(o => MatchesToken(o, words, index)),
            TokenKind.Not => false, // страж обрабатывается в MatchFrom, сюда не доходит
            _ => false,
        };
    }

    private static bool IsValid(SemanticPattern p)
    {
        // Not не может быть последним токеном.
        if (p.Tokens[^1].Kind == TokenKind.Not) return false;
        foreach (var t in p.Tokens)
        {
            if (t.Kind == TokenKind.AnyOf && t.Options is not { Count: > 0 }) return false;
            if (t.Kind == TokenKind.Not && t.Options is not { Count: > 0 }) return false;
        }
        return true;
    }

    /// <summary>
    /// Имя собственное: известная сущность каталога ИЛИ слово с заглавной
    /// буквы, стоящее не в начале предложения (эвристика без POS-теггера).
    /// </summary>
    private bool IsProper(string word, int positionInSentence)
    {
        if (_knownNames.Contains(word)) return true;
        if (positionInSentence == 0) return false;
        return IsCapitalized(word);
    }

    /// <summary>Слово с заглавной буквы в любой позиции предложения.</summary>
    private static bool IsCapitalized(string word)
    {
        if (word.Length == 0) return false;
        var first = word[0];
        return char.IsUpper(first) && char.IsLetter(first);
    }

    private static bool SafeIsMatch(string pattern, string input)
    {
        try { return Regex.IsMatch(input, pattern, RegexOptions.None, TimeSpan.FromMilliseconds(100)); }
        catch (ArgumentException) { return false; }
    }

    private static string Normalize(string s) => s.Trim().ToLowerInvariant();

    private static int CountLine(int[] lineStarts, int position)
    {
        var lo = 0;
        var hi = lineStarts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (lineStarts[mid] <= position) lo = mid;
            else hi = mid - 1;
        }
        return lo + 1;
    }
}

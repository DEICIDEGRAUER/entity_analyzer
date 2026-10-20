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
    // Regex-якоря: множество допустимых первых символов слова. Для
    // шаблона temporal-marker это цифры, поэтому regex больше не
    // запускается на каждом слове каждого предложения.
    private readonly List<(HashSet<char> FirstChars, int PatternIdx)> _regexAnchors = [];
    private readonly Dictionary<string, Regex?> _regexCache = new(StringComparer.Ordinal);
    private bool[] _legacyFlags = [];
    // Нормализованные литералы: Normalize() больше не вызывается на каждом
    // сравнении слова в горячем цикле сопоставления.
    private readonly Dictionary<string, string> _normalizedLiterals = new(StringComparer.Ordinal);

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
        foreach (var p in _patterns)
        {
            CacheLiteralValues(p.Tokens);
            CacheRegexes(p.Tokens);
        }
        CompileAnchors();
        _legacyFlags = new bool[_patterns.Count];
        foreach (var i in _legacyPatterns)
            _legacyFlags[i] = true;
    }

    /// <summary>Диагностика классификации: паттерн использует якорный индекс?</summary>
    internal bool IsAnchored(string patternId)
    {
        for (var i = 0; i < _patterns.Count; i++)
            if (string.Equals(_patterns[i].Id, patternId, StringComparison.OrdinalIgnoreCase))
                return !_legacyFlags[i];
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
            if (!t.Required || t.Min < 1 || t.Max > 1 || !AddAnchorToken(t, i))
                _legacyPatterns.Add(i);
        }
    }

    /// <summary>
    /// Добавляет якоря для токена. Для словаря якорями становятся все его
    /// однословные записи: многословные записи всё равно не могут совпасть
    /// с одним словом предложения. Regex-токены остаются легаси-путём.
    /// </summary>
    private bool AddAnchorToken(SemanticToken token, int patternIdx)
    {
        switch (token.Kind)
        {
            case TokenKind.Literal:
                AddAnchor(Normalize(token.Value), patternIdx);
                return true;

            case TokenKind.Lexicon:
                foreach (var word in _lexicons.WordsOf(token.Value))
                {
                    var normalized = Normalize(word);
                    if (normalized.Length == 0 || normalized.Any(char.IsWhiteSpace))
                        continue;
                    AddAnchor(normalized, patternIdx);
                }
                return true; // словарь без подходящих слов просто не даст кандидатов

            case TokenKind.Regex:
                if (!TryGetFirstChars(token.Value, out var firstChars))
                    return false;
                _regexAnchors.Add((firstChars, patternIdx));
                return true;

            case TokenKind.AnyOf when token.Options is { Count: > 0 }:
                var all = true;
                foreach (var option in token.Options)
                    all &= AddAnchorToken(option, patternIdx);
                return all;

            default:
                return false;
        }
    }

    private void AddAnchor(string word, int patternIdx)
    {
        if (!_anchorIndex.TryGetValue(word, out var list))
            _anchorIndex[word] = list = [];
        if (list.Count == 0 || list[^1] != patternIdx)
            list.Add(patternIdx);
    }

    private void CacheLiteralValues(IReadOnlyList<SemanticToken> tokens)
    {
        foreach (var t in tokens)
        {
            if (t.Kind == TokenKind.Literal)
                _normalizedLiterals[t.Value] = Normalize(t.Value);
            if (t.Options is { Count: > 0 })
                CacheLiteralValues(t.Options);
        }
    }

    private void CacheRegexes(IReadOnlyList<SemanticToken> tokens)
    {
        foreach (var t in tokens)
        {
            if (t.Kind == TokenKind.Regex && !_regexCache.ContainsKey(t.Value))
            {
                try
                {
                    _regexCache[t.Value] = new Regex(t.Value, RegexOptions.Compiled,
                        TimeSpan.FromMilliseconds(100));
                }
                catch (ArgumentException)
                {
                    _regexCache[t.Value] = null; // эквивалент SafeIsMatch(..., false)
                }
            }
            if (t.Options is { Count: > 0 })
                CacheRegexes(t.Options);
        }
    }

    /// <summary>
    /// Консервативно вычисляет множество первых символов regex-токена.
    /// Возвращает false для неанкоренных/сложных выражений — тогда паттерн
    /// остаётся на легаси-пути. Для шаблона временных маркеров множество
    /// сводится к цифрам, и regex не вызывается на каждом слове.
    /// </summary>
    private static bool TryGetFirstChars(string pattern, out HashSet<char> chars)
    {
        chars = [];
        if (pattern.Length == 0 || pattern[0] != '^')
            return false;
        var set = FirstCharsOfSequence(pattern, 0, pattern.Length);
        if (set is null) return false;
        chars = set;
        return true;
    }

    private static HashSet<char>? FirstCharsOfSequence(string s, int start, int end)
    {
        while (start < end && s[start] == '^') start++;
        if (start >= end) return null;

        var parts = new List<(int Start, int End)>();
        var partStart = start;
        var depth = 0;
        var inClass = false;
        for (var i = start; i < end; i++)
        {
            var c = s[i];
            if (c == '\\') { i++; continue; }
            if (inClass)
            {
                if (c == ']') inClass = false;
                continue;
            }
            if (c == '[') { inClass = true; continue; }
            if (c == '(') { depth++; continue; }
            if (c == ')')
            {
                if (depth == 0) return null;
                depth--;
                continue;
            }
            if (c == '|' && depth == 0)
            {
                parts.Add((partStart, i));
                partStart = i + 1;
            }
        }

        if (inClass || depth != 0) return null;
        if (parts.Count == 0)
            return FirstCharsOfAtom(s, start, end);

        parts.Add((partStart, end));
        var union = new HashSet<char>();
        foreach (var part in parts)
        {
            var set = FirstCharsOfSequence(s, part.Start, part.End);
            if (set is null) return null;
            union.UnionWith(set);
        }
        return union;
    }

    private static HashSet<char>? FirstCharsOfAtom(string s, int start, int end)
    {
        if (start >= end) return null;
        var c = s[start];
        var next = start + 1;
        HashSet<char> set;

        if (c == '(')
        {
            var close = FindClosingParen(s, start, end);
            if (close < 0) return null;
            var contentStart = start + 1;
            if (contentStart < close && s[contentStart] == '?')
            {
                if (contentStart + 1 < close && s[contentStart + 1] == ':')
                    contentStart += 2;
                else
                    return null; // lookaround/options/named group
            }
            var inner = FirstCharsOfSequence(s, contentStart, close);
            if (inner is null) return null;
            set = inner;
            next = close + 1;
        }
        else if (c == '[')
        {
            var classSet = FirstCharsOfClass(s, start, end, out next);
            if (classSet is null) return null;
            set = classSet;
        }
        else if (c == '\\')
        {
            if (start + 1 >= end) return null;
            var e = s[start + 1];
            if (e == 'd')
            {
                set = Digits();
                next = start + 2;
            }
            else if (e is 's' or 'n' or 'r' or 't' or 'f' or 'v' or 'a' or 'e')
            {
                // В слове из \p{L}\p{N} нет пробелов/управляющих символов.
                set = [];
                next = start + 2;
            }
            else if (e is 'D' or 'w' or 'W' or 'S' or 'p' or 'P' or 'b' or 'B' or
                     'A' or 'Z' or 'z' or 'G' or 'x' or 'c' or '0' or '1' or '2' or
                     '3' or '4' or '5' or '6' or '7')
            {
                return null;
            }
            else if (e == 'u' && start + 5 < end &&
                     ushort.TryParse(s.AsSpan(start + 2, 4),
                         System.Globalization.NumberStyles.HexNumber, null, out var code))
            {
                set = [(char)code];
                next = start + 6;
            }
            else if (char.IsLetterOrDigit(e))
            {
                return null; // неизвестный буквенный escape — не рискуем якорем
            }
            else
            {
                set = [e];
                next = start + 2;
            }
        }
        else if (c == '.')
        {
            return null;
        }
        else
        {
            set = [c];
        }

        // Квантор, способный сделать первый атом необязательным, ломает
        // якорь; обязательные (+, {n,}, {n,m} при n>=1) безопасны.
        if (next < end)
        {
            if (s[next] is '?' or '*') return null;
            if (s[next] == '{')
            {
                var qEnd = s.IndexOf('}', next + 1);
                if (qEnd < 0 || qEnd >= end) return null;
                var q = s[(next + 1)..qEnd];
                var comma = q.IndexOf(',');
                var minText = comma < 0 ? q : q[..comma];
                if (!int.TryParse(minText, out var min) || min < 1) return null;
                next = qEnd + 1;
            }
            else if (s[next] == '+')
            {
                next++;
            }
        }

        return set;
    }

    private static HashSet<char>? FirstCharsOfClass(string s, int start, int end, out int next)
    {
        next = start;
        var i = start + 1;
        if (i < end && s[i] is '^' or ']') return null; // отрицание/спецформа — не рискуем
        var set = new HashSet<char>();

        while (i < end && s[i] != ']')
        {
            if (s[i] == '\\')
            {
                if (i + 1 >= end) return null;
                var e = s[i + 1];
                if (e == 'd') set.UnionWith(Digits());
                else if (e is 'w' or 'W' or 's' or 'S' or 'D' or 'p' or 'P' or 'x' or 'c')
                    return null;
                else if (e is 'n' or 'r' or 't' or 'f' or 'v' or 'a' or 'e')
                    set.Add(e); // управляющий символ в слово не попадёт
                else if (char.IsLetterOrDigit(e))
                    return null;
                else
                    set.Add(e);
                i += 2;
                continue;
            }

            var first = s[i++];
            if (i + 1 < end && s[i] == '-' && s[i + 1] != ']')
            {
                var last = s[i + 1];
                i += 2;
                if (last < first || last - first > 512) return null;
                for (var ch = first; ch <= last; ch++) set.Add(ch);
            }
            else
            {
                set.Add(first);
            }
        }

        if (i >= end) return null;
        next = i + 1;
        return set;
    }

    private static int FindClosingParen(string s, int open, int end)
    {
        var depth = 1;
        var inClass = false;
        for (var i = open + 1; i < end; i++)
        {
            var c = s[i];
            if (c == '\\') { i++; continue; }
            if (inClass)
            {
                if (c == ']') inClass = false;
                continue;
            }
            if (c == '[') { inClass = true; continue; }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i;
        }
        return -1;
    }

    private static HashSet<char> Digits()
    {
        var set = new HashSet<char>(10);
        for (var c = '0'; c <= '9'; c++) set.Add(c);
        return set;
    }

    public bool HasPatterns => _patterns.Count > 0;

    /// <summary>Скан одного нормализованного текста.</summary>
    public List<SemanticMatch> Scan(string normalized, string filePath, int[] lineStarts)
    {
        var results = new List<SemanticMatch>();
        if (!HasPatterns) return results;

        var sentences = Text.SentenceSplitter.SplitWithSpans(normalized);
        var perPattern = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var words = new List<SemanticWord>(64);
        var candidates = new List<int>();
        var seen = new bool[_patterns.Count];
        var anchorStarts = new List<int>?[_patterns.Count];

        foreach (var sentence in sentences)
        {
            words.Clear();
            AddWords(normalized, sentence.Start, sentence.End, words);
            if (words.Count == 0) continue;

            // R1-4: кандидаты — только паттерны с якорем в этом предложении
            // (+ легаси-паттерны без якоря); порядок детерминированный.
            candidates.Clear();
            if (_useAnchors)
            {
                foreach (var pi in _legacyPatterns)
                {
                    seen[pi] = true;
                    candidates.Add(pi);
                }

                for (var wi = 0; wi < words.Count; wi++)
                {
                    if (_anchorIndex.TryGetValue(words[wi].Lower, out var hits))
                    {
                        foreach (var pi in hits)
                        {
                            if (!_legacyFlags[pi])
                            {
                                var starts = anchorStarts[pi] ??= new List<int>(4);
                                if (starts.Count == 0 || starts[^1] != wi)
                                    starts.Add(wi);
                            }
                            if (seen[pi]) continue;
                            seen[pi] = true;
                            candidates.Add(pi);
                        }
                    }

                    foreach (var (firstChars, pi) in _regexAnchors)
                    {
                        if (!firstChars.Contains(words[wi].Text[0])) continue;
                        if (!_legacyFlags[pi])
                        {
                            var starts = anchorStarts[pi] ??= new List<int>(4);
                            if (starts.Count == 0 || starts[^1] != wi)
                                starts.Add(wi);
                        }
                        if (seen[pi]) continue;
                        seen[pi] = true;
                        candidates.Add(pi);
                    }
                }

                candidates.Sort();
            }
            else
            {
                for (var pi = 0; pi < _patterns.Count; pi++)
                {
                    seen[pi] = true;
                    candidates.Add(pi);
                }
            }

            foreach (var pi in candidates)
            {
                var pattern = _patterns[pi];
                if (perPattern.TryGetValue(pattern.Id, out var count) &&
                    count >= _maxPerPatternPerFile)
                    continue;

                var starts = _legacyFlags[pi] ? null : anchorStarts[pi];
                if (TryMatchSequence(pattern, words, starts, out var startWord, out var endWord))
                {
                    var snippetStart = words[startWord].Offset;
                    var snippetEnd = Math.Min(sentence.End - sentence.Start,
                        words[endWord].Offset + words[endWord].Text.Length + 40);
                    var snippet = normalized.Substring(sentence.Start + snippetStart,
                        snippetEnd - snippetStart).Trim();
                    if (snippet.Length > 200) snippet = snippet[..200].Trim() + "…";

                    var absolutePos = sentence.Start + snippetStart;
                    var line = CountLine(lineStarts, absolutePos);

                    results.Add(new SemanticMatch(
                        pattern.Id, pattern.Name, filePath, line, snippet, absolutePos));

                    perPattern[pattern.Id] = count + 1;
                }
            }

            foreach (var pi in candidates)
            {
                seen[pi] = false;
                anchorStarts[pi]?.Clear();
            }
        }

        return results;
    }

    private readonly record struct SemanticWord(string Text, string Lower, int Offset);

    private static bool IsWordChar(char c) => char.IsLetter(c) || char.IsNumber(c);

    /// <summary>
    /// Ручной эквивалент WordRegex ([\p{L}\p{N}]+): без Match-аллокаций
    /// и без промежуточной подстроки всего предложения.
    /// </summary>
    private static void AddWords(string text, int start, int end, List<SemanticWord> words)
    {
        var i = start;
        while (i < end)
        {
            if (!IsWordChar(text[i])) { i++; continue; }

            var wordStart = i;
            i++;
            while (i < end && IsWordChar(text[i])) i++;

            var value = text[wordStart..i];
            words.Add(new SemanticWord(value, value.ToLowerInvariant(), wordStart - start));
        }
    }

    /// <summary>
    /// Поиск последовательности: рекурсия с бэктрекингом обрабатывает
    /// опциональные токены, альтернативы и стражи Not; общее окно
    /// TotalWindow проверяется в момент завершения цепочки.
    /// </summary>
    internal bool TryMatchSequence(SemanticPattern pattern,
        List<(string Text, int Offset)> words, out int startIdx, out int endIdx)
    {
        // Совместимый вход для тестов/внешних вызовов; рабочий Scan использует
        // уже подготовленные SemanticWord с готовым нижним регистром.
        var prepared = new List<SemanticWord>(words.Count);
        foreach (var w in words)
            prepared.Add(new SemanticWord(w.Text, w.Text.ToLowerInvariant(), w.Offset));
        return TryMatchSequence(pattern, prepared, null, out startIdx, out endIdx);
    }

    private bool TryMatchSequence(SemanticPattern pattern,
        List<SemanticWord> words, List<int>? firstStarts, out int startIdx, out int endIdx)
    {
        if (MatchFrom(pattern, 0, words, -1, -1, firstStarts, out startIdx, out endIdx))
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
        List<SemanticWord> words, int cursor, int firstIdxIn,
        List<int>? firstStarts,
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
            MatchFrom(p, ti + 1, words, cursor, firstIdxIn, null, out firstIdx, out endIdx))
            return true;

        if (cursor < 0)
        {
            // Первый потребляющий токен паттерна: для якорных паттернов
            // перебираем только позиции слов-якорей, иначе — всё предложение.
            var startCount = firstStarts?.Count ?? words.Count;
            for (var si = 0; si < startCount; si++)
            {
                var s = firstStarts is null ? si : firstStarts[si];
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

                    if (MatchFrom(p, ti + 1, words, s + k - 1, s, null, out firstIdx, out endIdx))
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

                    if (MatchFrom(p, ti + 1, words, s + k - 1, firstIdxIn, null, out firstIdx, out endIdx))
                        return true;
                }
            }
        }

        firstIdx = firstIdxIn;
        endIdx = -1;
        return false;
    }

    /// <summary>Потребить k подряд идущих слов, соответствующих токену, начиная с позиции start.</summary>
    private bool TryConsumeK(SemanticToken token, List<SemanticWord> words,
        int start, int k)
    {
        for (var n = 0; n < k; n++)
        {
            if (start + n >= words.Count) return false;
            if (!MatchesToken(token, words, start + n)) return false;
        }
        return true;
    }

    private bool MatchesToken(SemanticToken token, List<SemanticWord> words, int index)
    {
        var word = words[index];

        return token.Kind switch
        {
            TokenKind.Literal => string.Equals(word.Lower,
                _normalizedLiterals.TryGetValue(token.Value, out var normalized)
                    ? normalized
                    : Normalize(token.Value),
                StringComparison.Ordinal),
            TokenKind.Lexicon => _lexicons.Contains(token.Value, word.Text),
            TokenKind.Regex => RegexMatches(token.Value, word.Text),
            TokenKind.AnyWord => true,
            TokenKind.Proper => IsProper(word.Text, index),
            TokenKind.Capitalized => IsCapitalized(word.Text),
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

    private bool RegexMatches(string pattern, string input)
    {
        if (_regexCache.TryGetValue(pattern, out var regex))
        {
            if (regex is null) return false;
            try { return regex.IsMatch(input); }
            catch (ArgumentException) { return false; }
        }
        return SafeIsMatch(pattern, input);
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

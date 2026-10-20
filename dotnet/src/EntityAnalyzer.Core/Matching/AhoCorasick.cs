using System.Collections.Generic;

namespace EntityAnalyzer.Core.Matching;

/// <summary>Result of a pattern occurrence found in the searched text.</summary>
/// <remarks><paramref name="Start"/> is inclusive, <paramref name="End"/> is exclusive.</remarks>
public readonly record struct AhoMatch<T>(int Start, int End, string Pattern, T Payload);

/// <summary>
/// Aho–Corasick multi-pattern string matching automaton.
/// Case-insensitive (ordinal lowercasing); enumerates every occurrence including overlaps,
/// mirroring the behaviour of the original Python <c>pyahocorasick</c>-based engine.
/// </summary>
public sealed class AhoCorasick<T>
{
    private sealed class Node
    {
        public Dictionary<char, int> Children { get; } = new();
        public int Fail { get; set; }
        /// <summary>Nearest node reachable via failure links that carries outputs (-1 if none).</summary>
        public int SuffixOutput { get; set; } = -1;
        /// <summary>Patterns ending exactly at this node, in insertion order.</summary>
        public List<(string Original, T Payload)>? Outputs { get; set; }
    }

    private readonly List<Node> _nodes = [new()];
    private bool _built;

    public bool IsBuilt => _built;
    public int PatternCount { get; private set; }

    // Плотные переходы по алфавиту паттернов: один array lookup на символ
    // вместо Dictionary + подъёма по failure-ссылкам.
    private int[][] _go = [];
    private int[] _symbolOf = [];


    /// <summary>Adds a pattern. Empty or null patterns are ignored. Call before <see cref="Build"/>.</summary>
    public void Add(string pattern, T payload)
    {
        if (_built) throw new InvalidOperationException("Cannot add patterns after Build().");
        ArgumentException.ThrowIfNullOrEmpty(pattern);

        var key = pattern.ToLowerInvariant();
        var node = 0;
        foreach (var ch in key)
        {
            if (!_nodes[node].Children.TryGetValue(ch, out var next))
            {
                next = _nodes.Count;
                _nodes[node].Children.Add(ch, next);
                _nodes.Add(new Node());
            }
            node = next;
        }

        _nodes[node].Outputs ??= new List<(string, T)>();
        _nodes[node].Outputs!.Add((pattern, payload));
        PatternCount++;
    }

    /// <summary>Finalizes the automaton. Required before searching.</summary>
    public void Build()
    {
        if (_built) return;

        // Breadth-first construction of failure links.
        var queue = new Queue<int>();
        foreach (var child in _nodes[0].Children.Values)
        {
            _nodes[child].Fail = 0;
            queue.Enqueue(child);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            var curNode = _nodes[current];

            // Compute nearest output-bearing node along the failure chain for fast enumeration.
            var f = curNode.Fail;
            curNode.SuffixOutput = _nodes[f].Outputs is not null ? f : _nodes[f].SuffixOutput;

            foreach (var (ch, childIdx) in curNode.Children)
            {
                var candidate = curNode.Fail;
                while (candidate != 0 && !_nodes[candidate].Children.ContainsKey(ch))
                    candidate = _nodes[candidate].Fail;

                if (_nodes[candidate].Children.TryGetValue(ch, out var target) && target != childIdx)
                    _nodes[childIdx].Fail = target;
                else
                    _nodes[childIdx].Fail = 0;

                queue.Enqueue(childIdx);
            }
        }

        // Компактный алфавит паттернов и полные goto-переходы.
        var alphabet = new List<char>();
        foreach (var node in _nodes)
            foreach (var ch in node.Children.Keys)
                if (!alphabet.Contains(ch)) alphabet.Add(ch);
        alphabet.Sort();

        var symbol = new Dictionary<char, int>(alphabet.Count);
        for (var i = 0; i < alphabet.Count; i++) symbol[alphabet[i]] = i;

        _go = new int[_nodes.Count][];
        var rootRow = new int[alphabet.Count];
        foreach (var (ch, child) in _nodes[0].Children)
            rootRow[symbol[ch]] = child;
        _go[0] = rootRow;

        var order = new Queue<int>();
        foreach (var child in _nodes[0].Children.Values) order.Enqueue(child);
        while (order.Count > 0)
        {
            var current = order.Dequeue();
            var row = new int[alphabet.Count];
            for (var sym = 0; sym < alphabet.Count; sym++)
            {
                if (_nodes[current].Children.TryGetValue(alphabet[sym], out var child))
                    row[sym] = child;
                else
                    row[sym] = _go[_nodes[current].Fail][sym];
            }
            _go[current] = row;
            foreach (var child in _nodes[current].Children.Values) order.Enqueue(child);
        }

        // Для обычных символов посимвольный lower совпадает со строковым;
        // редкие контекстные случаи уходят в точный строковый путь в Find.
        _symbolOf = new int[char.MaxValue + 1];
        Array.Fill(_symbolOf, -1);
        for (var i = 0; i < _symbolOf.Length; i++)
        {
            var lower = char.ToLowerInvariant((char)i);
            if (symbol.TryGetValue(lower, out var sym))
                _symbolOf[i] = sym;
        }

        _built = true;
    }

    /// <summary>
    /// Finds all occurrences of all added patterns in <paramref name="text"/>.
    /// Matches are yielded ordered by end position.
    /// </summary>
    public IEnumerable<AhoMatch<T>> Find(string text)
    {
        var results = new List<AhoMatch<T>>();
        Find(text, results.Add);
        return results;
    }

    /// <summary>
    /// Callback-вариант без итератора: движок сразу складывает совпадения
    /// в свой список.
    /// </summary>
    public void Find(string text, Action<AhoMatch<T>> onMatch)
    {
        ArgumentNullException.ThrowIfNull(onMatch);
        if (!_built) throw new InvalidOperationException("Call Build() before Find().");
        if (string.IsNullOrEmpty(text)) return;

        // Быстрый путь — посимвольный lower; не-BMP-пары обрабатывает
        // точный строковый ToLowerInvariant.
        var fullLower = text.AsSpan().IndexOfAnyInRange('\uD800', '\uDFFF') >= 0;
        var source = fullLower ? text.ToLowerInvariant() : text;
        var state = 0;

        for (var i = 0; i < source.Length; i++)
        {
            var sym = _symbolOf[source[i]];
            if (sym < 0)
            {
                // Символа нет ни в одном паттерне — из любого состояния
                // переход ведёт в корень, где выходов нет.
                state = 0;
                continue;
            }

            state = _go[state][sym];
            if (state == 0) continue;

            // Enumerate terminal outputs at this state and every output-bearing suffix state.
            var s = _nodes[state].Outputs is not null ? state : _nodes[state].SuffixOutput;
            while (s != -1)
            {
                foreach (var (original, payload) in _nodes[s].Outputs!)
                {
                    var len = original.Length;
                    var start = i - len + 1;
                    if (start >= 0)
                        onMatch(new AhoMatch<T>(start, i + 1, original, payload));
                }
                s = _nodes[s].SuffixOutput;
            }
        }
    }
}

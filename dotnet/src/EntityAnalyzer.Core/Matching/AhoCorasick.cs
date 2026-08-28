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

        _built = true;
    }

    /// <summary>
    /// Finds all occurrences of all added patterns in <paramref name="text"/>.
    /// Matches are yielded ordered by end position.
    /// </summary>
    public IEnumerable<AhoMatch<T>> Find(string text)
    {
        if (!_built) throw new InvalidOperationException("Call Build() before Find().");
        if (string.IsNullOrEmpty(text)) yield break;

        var lowered = text.ToLowerInvariant();
        var state = 0;

        for (var i = 0; i < lowered.Length; i++)
        {
            var ch = lowered[i];

            while (state != 0 && !_nodes[state].Children.TryGetValue(ch, out _))
                state = _nodes[state].Fail;

            if (_nodes[state].Children.TryGetValue(ch, out var next))
                state = next;
            else
                state = 0;

            // Enumerate terminal outputs at this state and every output-bearing suffix state.
            var s = _nodes[state].Outputs is not null ? state : _nodes[state].SuffixOutput;
            while (s != -1)
            {
                foreach (var (original, payload) in _nodes[s].Outputs!)
                {
                    var len = original.Length;
                    var start = i - len + 1;
                    if (start >= 0)
                        yield return new AhoMatch<T>(start, i + 1, original, payload);
                }
                s = _nodes[s].SuffixOutput;
            }
        }
    }
}

using System.Text;

namespace EntityAnalyzer.Core.Variants;

/// <summary>
/// Deterministic Latin→Cyrillic transliterator with common digraph handling.
/// Initial/mid "y" before a vowel becomes «й» (Nyarlathotep → Ньярлатотеп),
/// otherwise «и» — closer to how the mythos names are actually rendered in Russian.
/// </summary>
public static class Transliterator
{
    private static readonly (string Eng, string Rus)[] Digraphs =
    [
        ("th", "т"), ("ch", "ч"), ("sh", "ш"),
        ("kh", "кх"), ("gh", "г"), ("ph", "ф"), ("ts", "ц")
    ];

    private static readonly Dictionary<char, string> Singles = new()
    {
        ['a'] = "а", ['b'] = "б", ['c'] = "к", ['d'] = "д", ['e'] = "е", ['f'] = "ф",
        ['g'] = "г", ['h'] = "х", ['i'] = "и", ['j'] = "дж", ['k'] = "к", ['l'] = "л",
        ['m'] = "м", ['n'] = "н", ['o'] = "о", ['p'] = "п", ['q'] = "к", ['r'] = "р",
        ['s'] = "с", ['t'] = "т", ['u'] = "у", ['v'] = "в", ['w'] = "в", ['x'] = "кс",
        ['z'] = "з"
    };

    private const string Vowels = "aeiou";

    public static string Basic(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;

        var sb = new StringBuilder(name.Length);
        var i = 0;
        while (i < name.Length)
        {
            var cap = char.IsUpper(name[i]);
            var two = i + 1 < name.Length
                ? char.ToLowerInvariant(name[i]).ToString() + char.ToLowerInvariant(name[i + 1])
                : null;

            if (two is not null && TryDigraph(two, out var rus))
            {
                Append(sb, rus, cap);
                i += 2;
                continue;
            }

            var c = char.ToLowerInvariant(name[i]);
            if (c == 'y')
            {
                var nextVowel = i + 1 < name.Length && Vowels.Contains(char.ToLowerInvariant(name[i + 1]));
                Append(sb, nextVowel ? "й" : "и", cap);
                i++;
                continue;
            }
            if (Singles.TryGetValue(c, out var single))
            {
                Append(sb, single, cap);
                i++;
                continue;
            }
            sb.Append(name[i]); // unknown chars pass through (spaces, hyphens…)
            i++;
        }
        return sb.ToString();
    }

    private static bool TryDigraph(string pair, out string rus)
    {
        foreach (var d in Digraphs)
            if (d.Eng == pair) { rus = d.Rus; return true; }
        rus = "";
        return false;
    }

    private static void Append(StringBuilder sb, string rus, bool capitalizeFirst)
    {
        if (!capitalizeFirst) { sb.Append(rus); return; }
        sb.Append(char.ToUpperInvariant(rus[0]));
        if (rus.Length > 1) sb.Append(rus.AsSpan(1));
    }
}

/// <summary>Bounded mutation engine simulating OCR/typo corruption of names.</summary>
public static class Corruptor
{
    private static readonly (char A, char B)[] Mutations =
    [
        ('т', 'ф'), ('с', 'з'), ('к', 'г'), ('х', 'г'), ('б', 'п'), ('п', 'б'), ('д', 'т')
    ];

    private const string RuVowels = "аеёиоуыэюя";

    public static IReadOnlyList<string> Corrupt(string name)
    {
        var results = new List<string>();
        void Add(string s) { if (!string.IsNullOrEmpty(s) && !results.Contains(s)) results.Add(s); }

        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            var lower = char.ToLowerInvariant(c);

            // Consonant mutations, both directions, case-preserving.
            foreach (var (a, b) in Mutations)
            {
                if (lower == a)
                    Add(MutateAt(name, i, b));
                else if (lower == b)
                    Add(MutateAt(name, i, a));
            }

            // Single-character deletion.
            Add(name.Remove(i, 1));

            // «Х» inserted right after a vowel.
            if (RuVowels.Contains(lower))
                Add(name.Insert(i + 1, "х"));

            // Doubling of a consonant.
            if (char.IsLetter(c) && !RuVowels.Contains(lower))
                Add(name.Insert(i + 1, c.ToString()));
        }
        return results;
    }

    private static string MutateAt(string name, int i, char replacement) =>
        name.Substring(0, i) +
        (char.IsUpper(name[i]) ? char.ToUpperInvariant(replacement) : replacement) +
        name.Substring(i + 1);
}

/// <summary>
/// Produces the full pool of spelling variants for one canonical English name:
/// transliteration, orthographic rules, hyphen/space shapes, Russian declensions
/// and optional corruption — deterministic, insertion-ordered, hard-capped.
/// </summary>
public sealed class VariantFactory
{
    private readonly int _maxVariants;
    private const string RuVowels = "аеёиоуыэюя";

    public VariantFactory(int maxVariants = 600) => _maxVariants = Math.Max(8, maxVariants);

    public List<string> Generate(string canon, bool deepCorrupt = true, int iterations = 1)
    {
        if (string.IsNullOrWhiteSpace(canon)) return [];

        var acc = new OrderedSet();
        var isCompound = canon.Contains(' ');

        void Absorb(IEnumerable<string> source)
        {
            foreach (var s in source)
                if (s.Length >= 3) acc.Add(s);
        }

        Absorb([Transliterator.Basic(canon)]);
        Absorb(ApplyOrthographicRules(canon));
        Absorb(HyphenShapes(canon));
        Absorb(HyphenShapes(Transliterator.Basic(canon)));

        // Iterations deepen the pool by reshaping what we already have.
        var frontier = new List<string>(acc.Items);
        for (var round = 0; round < Math.Max(0, iterations - 1) && frontier.Count > 0; round++)
        {
            var next = new List<string>();
            foreach (var item in frontier.Take(24))
            {
                next.AddRange(HyphenShapes(item));
                Absorb(RussianMorphology.Decline(item));
                Absorb(DeclineRussian(item, allowCompound: false));
            }
            Absorb(next);
            frontier = next;
        }

        // Declensions for short non-compound candidates (полная морфология).
        foreach (var v in acc.Items.Take(40))
        {
            Absorb(DeclineRussian(v, allowCompound: isCompound));
            if (!v.Contains(' '))
                Absorb(RussianMorphology.Decline(v));
        }

        // Составные каноны («Наг и Йеб»): склоняем каждое значимое слово отдельно.
        foreach (var form in RussianMorphology.DeclineCompound(canon))
            Absorb([form]);

        if (deepCorrupt && !isCompound)
        {
            foreach (var v in acc.Items.Take(30))
                Absorb(Corruptor.Corrupt(v));
        }

        return acc.Items.Where(s => s.Length >= 3).Take(_maxVariants).ToList();
    }

    /// <summary>Narrow = curated irregulars + basic translit; Wide = generated corpus.</summary>
    public (List<string> Narrow, List<string> Wide) GenerateTwoPass(
        string canon, IReadOnlyList<string> irregular, bool isCommonWord, int iterations = 2)
    {
        var basic = Transliterator.Basic(canon);
        var narrow = new List<string>();
        foreach (var v in irregular) if (v.Length >= 3 && !narrow.Contains(v)) narrow.Add(v);
        if (basic.Length >= 3 && !narrow.Contains(basic)) narrow.Add(basic);

        var wide = Generate(canon,
            deepCorrupt: canon.Length > 6 && !isCommonWord,
            iterations: canon.Length > 6 ? iterations : 1);

        return (narrow, wide);
    }

    private static IEnumerable<string> ApplyOrthographicRules(string name)
    {
        var results = new List<string>();
        void AddRule((string From, string[] To) rule)
        {
            foreach (var to in rule.To)
                if (name.Contains(rule.From, StringComparison.OrdinalIgnoreCase))
                    results.Add(name.Replace(rule.From, to, StringComparison.OrdinalIgnoreCase));
        }

        AddRule(("th", ["т", "тх", "с", "ф"]));
        AddRule(("gh", ["г", "гх", "х"]));
        AddRule(("kh", ["кх", "х"]));

        var lower = name.ToLowerInvariant();
        if (lower.EndsWith("oa")) results.AddRange([name[..^2] + "о", name[..^2] + "ос", name[..^2] + "от"]);
        if (lower.EndsWith("ia")) results.AddRange([name[..^2] + "ия", name[..^2] + "иа"]);
        if (lower.EndsWith("ea")) results.AddRange([name[..^2] + "еа", name[..^2] + "ея"]);

        AddRule(("ge", ["же"]));
        AddRule(("gi", ["жи"]));
        AddRule(("gy", ["жи"]));

        return results;
    }

    internal static IEnumerable<string> HyphenShapes(string name)
    {
        var results = new List<string>();
        if (name.Contains('-'))
        {
            results.Add(name.Replace('-', ' '));
            results.Add(name.Replace("-", ""));
        }
        if (name.Contains(' '))
        {
            results.Add(name.Replace(' ', '-'));
            results.Add(name.Replace(" ", ""));
        }
        return results.Distinct();
    }

    internal static IEnumerable<string> DeclineRussian(string name, bool allowCompound)
    {
        if (allowCompound || name.Length == 0 || name.Contains(' ')) yield break;

        var tail = name[^Math.Min(2, name.Length)..].ToLowerInvariant();
        var hasVowel = tail.IndexOfAny(RuVowels.ToCharArray()) >= 0;
        if (!hasVowel) yield break;

        var last = char.ToLowerInvariant(name[^1]);
        var stem = name[..^1];
        switch (last)
        {
            case 'а':
                foreach (var e in new[] { "ы", "е", "у", "ой" }) yield return stem + e;
                break;
            case 'я':
                foreach (var e in new[] { "и", "е", "ю", "ей" }) yield return stem + e;
                break;
            case 'ь':
                foreach (var e in new[] { "я", "ю", "ем", "е" }) yield return stem + e;
                break;
            default:
                if (!RuVowels.Contains(last) && last != 'ь')
                    foreach (var e in new[] { "а", "у", "ом", "е" }) yield return name + e;
                break;
        }
    }

    /// <summary>Insertion-ordered set keeping first occurrences.</summary>
    private sealed class OrderedSet
    {
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        public List<string> Items { get; } = [];
        public void Add(string s)
        {
            if (_seen.Add(s)) Items.Add(s);
        }
    }
}

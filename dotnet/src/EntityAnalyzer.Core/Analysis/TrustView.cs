using System.Text.RegularExpressions;
using EntityAnalyzer.Core.Codex;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>
/// EXPLORATORY trust classes for the optional "trusted" result view.
///
/// This is deliberately a conservative lexical rule, not an owner table:
///  • canonical exact name/canonical is trusted;
///  • a multi-word alias whose significant tokens are part of the
///    name/canonical (or are single-word aliases of the same card) is strong;
///  • a single-word alias may be strong only when it is a close form of a
///    one-word name/canonical (e.g. «Гули» for «Гуль», «Чо-Чо» for «Тчо-Тчо»);
///  • generic titles like «The Gate» stay weak/composite even if an anchor
///    happens to be present;
///  • a strong alias is trusted only when it is a full phrase or the context
///    contains another independent anchor of the same card.
///
/// Probe 2's frozen labels selected exactly the same 14 rows as the
/// marker-gated strong/canonical bucket (P=1.000, Wilson lower 0.785).
/// That makes this suitable for an explicit *exploratory* filter, not for a
/// production SLO or a silent default-view filter.
/// </summary>
public enum TrustViewLevel
{
    Unknown,
    Weak,
    StrongAlias,
    Canonical,
}

public sealed record TrustViewAssessment(
    TrustViewLevel Level,
    bool Marker,
    bool IsTrusted,
    string Reason);

public sealed class TrustViewClassifier
{
    private static readonly Regex TokenRegex = new(
        @"[\p{L}\p{N}]+(?:[-'][\p{L}\p{N}]+)*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Generic head words that must not make a different owner's phrase
    /// "strong" merely because the same common word appears in the card name.
    /// </summary>
    private static readonly HashSet<string> GenericTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "of", "and", "a", "an", "people", "book", "plateau", "garden",
        "monks", "народ", "книга", "плато", "сад", "монахи", "и", "в", "на", "с",
    };

    private readonly Dictionary<string, Card> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _singleAliasTokens = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, HashSet<string>> _owners = new(StringComparer.OrdinalIgnoreCase);

    public static TrustViewClassifier Empty { get; } = new([]);

    private TrustViewClassifier(IEnumerable<Card> cards)
    {
        foreach (var card in cards)
        {
            if (!card.Enabled || string.IsNullOrWhiteSpace(card.Name)) continue;
            _cards[card.Name] = card;
            var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var alias in card.Aliases)
            {
                var raw = RawTokens(alias);
                if (raw.Count == 1 && raw[0].Length >= 3 && !GenericTokens.Contains(raw[0]))
                    tokens.Add(raw[0]);
            }
            _singleAliasTokens[card.Name] = tokens;

            foreach (var owned in card.Aliases.Concat([card.Name, card.Canonical]))
            {
                var key = Normalize(owned);
                if (key.Length == 0) continue;
                if (!_owners.TryGetValue(key, out var owners))
                    _owners[key] = owners = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                owners.Add(card.Name);
            }
        }
    }

    public static TrustViewClassifier FromCodex(CodexDocument? document) =>
        document is null ? Empty : new TrustViewClassifier(document.Cards);

    public TrustViewAssessment Assess(Mention mention) =>
        Assess(mention.Entity, mention.Variant, mention.Context);

    public TrustViewAssessment Assess(string entity, string variant, string context)
    {
        if (!_cards.TryGetValue(entity, out var card))
            return new(TrustViewLevel.Unknown, false, false, "not in catalog");

        var value = Normalize(variant);
        var name = Normalize(card.Name);
        var canonical = Normalize(card.Canonical);

        if (value.Length == 0)
            return new(TrustViewLevel.Unknown, false, false, "empty variant");

        if (value == name || (canonical.Length > 0 && value == canonical))
        {
            if (OwnerCount(value) > 1)
                return new(TrustViewLevel.Weak, false, false, "shared canonical/alias");
            return new(TrustViewLevel.Canonical, true, true, "canonical");
        }

        var aliases = card.Aliases
            .Select(Normalize)
            .Where(a => a.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!aliases.Contains(value))
            return new(TrustViewLevel.Weak, false, false, "generated or unrecognized form");

        if (OwnerCount(value) > 1)
            return new(TrustViewLevel.Weak, false, false, "shared alias/owner conflict");

        var raw = RawTokens(value);
        var significant = SignificantTokens(value);
        var distinctive = NameTokens(card);

        var strong = false;
        if (raw.Count >= 2 && significant.Count > 0)
        {
            // Every significant word must belong to this card: either a
            // distinctive token from name/canonical, or a one-word alias.
            var aliasTokens = _singleAliasTokens.GetValueOrDefault(card.Name) ?? [];
            strong = significant.All(t =>
                distinctive.Any(d => Near(t, d)) || aliasTokens.Contains(t));
        }
        else if (raw.Count == 1 && raw[0].Length >= 4)
        {
            // A short/morphological alias is strong only for one-word names.
            if (RawTokens(card.Name).Count == 1 && RawTokens(card.Canonical).Count <= 1)
                strong = NameTokens(card).Any(d => Near(value, d));
        }

        if (!strong)
            return new(TrustViewLevel.Weak, false, false, "weak/composite alias");

        var marker = raw.Count >= 2 || HasContextAnchor(card, value, context);
        return marker
            ? new(TrustViewLevel.StrongAlias, true, true, "strong alias + marker")
            : new(TrustViewLevel.StrongAlias, false, false, "strong alias without marker");
    }

    private bool HasContextAnchor(Card card, string variant, string context)
    {
        if (string.IsNullOrWhiteSpace(context)) return false;

        foreach (var anchor in card.Aliases.Concat([card.Name, card.Canonical]))
        {
            var normalized = Normalize(anchor);
            if (normalized.Length < 3 || normalized == variant) continue;
            if (context.Contains(normalized, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private int OwnerCount(string value) =>
        _owners.TryGetValue(value, out var owners) ? owners.Count : 0;

    private static HashSet<string> NameTokens(Card card)
    {
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var token in RawTokens(card.Name).Concat(RawTokens(card.Canonical)))
            if (token.Length >= 3 && !GenericTokens.Contains(token))
                tokens.Add(token);
        return tokens;
    }

    private static List<string> RawTokens(string value) =>
        TokenRegex.Matches(value ?? "")
            .Select(m => m.Value.ToLowerInvariant())
            .Where(t => t.Length > 0)
            .ToList();

    private static List<string> SignificantTokens(string value) =>
        RawTokens(value)
            .Where(t => t.Length >= 3 && !GenericTokens.Contains(t))
            .ToList();

    private static string Normalize(string? value) =>
        (value ?? "").Trim().ToLowerInvariant();

    /// <summary>
    /// Deliberately conservative cross-form comparison. Short forms (&lt;4)
    /// never match by edit distance; this keeps «Лао» from becoming a strong
    /// alias of «Ляо» while allowing «Чо-Чо» / «Tcho-Tcho».
    /// </summary>
    private static bool Near(string a, string b)
    {
        if (a.Length < 4 || b.Length < 4) return false;
        if (a.Equals(b, StringComparison.OrdinalIgnoreCase)) return true;
        if (CommonPrefix(a, b) >= 3) return true;

        var distance = Levenshtein(a, b);
        var max = Math.Max(a.Length, b.Length);
        return distance <= 2 && (double)distance / max <= 0.30;
    }

    private static int CommonPrefix(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }
        return previous[b.Length];
    }
}

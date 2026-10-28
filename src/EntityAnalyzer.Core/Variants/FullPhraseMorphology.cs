using System.Text;

namespace EntityAnalyzer.Core.Variants;

/// <summary>
/// Declines a Russian composite name as one phrase. The existing
/// <see cref="RussianMorphology.DeclineCompound"/> declines every component
/// separately, which is exactly the source of bare-head/bare-tail noise. This
/// helper keeps the phrase together and only declines the components that are
/// grammatical in the phrase.
/// </summary>
public static class FullPhraseMorphology
{
    private static readonly HashSet<string> IndeclinableFirst = new(StringComparer.OrdinalIgnoreCase)
    {
        "плато",
    };

    private static readonly HashSet<string> PluralHeads = new(StringComparer.OrdinalIgnoreCase)
    {
        "монахи", "боги",
    };

    public static IEnumerable<string> Decline(string phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase)) yield break;

        var text = phrase.Trim();
        if (!HasCyrillic(text)) yield break;
        if (text.IndexOfAny([',', ';', ':', '(', ')', '—']) >= 0) yield break;

        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) yield break;

        var conjunction = Array.FindIndex(parts, p => p.Equals("и", StringComparison.OrdinalIgnoreCase));
        if (conjunction > 0 && conjunction < parts.Length - 1)
        {
            var left = string.Join(" ", parts[..conjunction]);
            var right = string.Join(" ", parts[(conjunction + 1)..]);
            foreach (var form in Combine(
                         RussianMorphology.Decline(left).ToList(),
                         RussianMorphology.Decline(right).ToList(),
                         " и "))
                yield return form;
            yield break;
        }

        var first = parts[0];
        var last = parts[^1];
        var prefix = parts[..^1];

        if (IsAdjective(first))
        {
            foreach (var form in Combine(
                         AdjectiveForms(first),
                         NounForms(last),
                         " "))
                yield return form;
            yield break;
        }

        if (IndeclinableFirst.Contains(first))
        {
            foreach (var form in RussianMorphology.Decline(last))
                yield return string.Join(" ", prefix) + " " + form;
            yield break;
        }

        if (PluralHeads.Contains(first))
        {
            foreach (var form in PluralForms(first))
                yield return form + " " + string.Join(" ", parts[1..]);
            yield break;
        }

        // Noun + genitive modifier: Книга Эйбона, Сад Йига, Монахи Ленга.
        if (EndsInGenitive(last))
        {
            foreach (var form in RussianMorphology.Decline(first))
                yield return form + " " + string.Join(" ", parts[1..]);
            yield break;
        }

        // Two proper names: Куачиль Уттаус.
        var lists = parts
            .Select(p => RussianMorphology.Decline(p).ToList())
            .ToList();
        if (lists.Any(l => l.Count == 0)) yield break;

        var count = lists.Min(l => l.Count);
        for (var i = 0; i < count; i++)
            yield return string.Join(" ", lists.Select(l => l[i]));
    }

    private static IEnumerable<string> Combine(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second,
        string separator)
    {
        var count = Math.Min(first.Count, second.Count);
        for (var i = 0; i < count; i++)
            yield return first[i] + separator + second[i];
    }

    private static IReadOnlyList<string> AdjectiveForms(string word)
    {
        var lower = word.ToLowerInvariant();
        var stemLength = lower.EndsWith("ый", StringComparison.Ordinal) ||
                         lower.EndsWith("ий", StringComparison.Ordinal) ||
                         lower.EndsWith("ое", StringComparison.Ordinal) ||
                         lower.EndsWith("ая", StringComparison.Ordinal) ||
                         lower.EndsWith("ые", StringComparison.Ordinal) ||
                         lower.EndsWith("ие", StringComparison.Ordinal)
            ? 2
            : 0;
        if (stemLength == 0) return [];

        var stem = word[..^stemLength];
        if (lower.EndsWith("ое", StringComparison.Ordinal) ||
            lower.EndsWith("ая", StringComparison.Ordinal) ||
            lower.EndsWith("ые", StringComparison.Ordinal) ||
            lower.EndsWith("ие", StringComparison.Ordinal))
            return [stem + "ого", stem + "ому", stem + "ым", stem + "ом"];

        return [stem + "ого", stem + "ому", stem + "ым", stem + "ом"];
    }

    private static IReadOnlyList<string> NounForms(string word)
    {
        var forms = RussianMorphology.Decline(word).ToList();
        if (forms.Count > 0) return forms;

        // Neuter -е/-о: Солнце → Солнца, Солнцу, Солнцем, Солнце.
        var lower = word.ToLowerInvariant();
        if (lower.EndsWith('е') || lower.EndsWith('о'))
        {
            var stem = word[..^1];
            return [stem + "а", stem + "у", stem + "ем", word];
        }
        return [];
    }

    private static IEnumerable<string> PluralForms(string word)
    {
        if (word.Length < 2) yield break;
        var stem = word[..^1];
        yield return stem + "ов";
        yield return stem + "ам";
        yield return stem + "ами";
        yield return stem + "ах";
    }

    private static bool EndsInGenitive(string word)
    {
        var lower = word.ToLowerInvariant();
        return lower.EndsWith('а') || lower.EndsWith('я');
    }

    private static bool IsAdjective(string word)
    {
        var lower = word.ToLowerInvariant();
        return lower.EndsWith("ый", StringComparison.Ordinal) ||
               lower.EndsWith("ий", StringComparison.Ordinal) ||
               lower.EndsWith("ое", StringComparison.Ordinal) ||
               lower.EndsWith("ая", StringComparison.Ordinal) ||
               lower.EndsWith("ые", StringComparison.Ordinal) ||
               lower.EndsWith("ие", StringComparison.Ordinal);
    }

    private static bool HasCyrillic(string text) =>
        text.Any(c => c is >= 'А' and <= 'я' or 'Ё' or 'ё');
}

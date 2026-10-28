namespace EntityAnalyzer.Core.Variants;

/// <summary>
/// Variant-generation policy for composite names. Legacy reproduces the old
/// per-component declension; the full-phrase modes keep the phrase together so
/// a composite never emits a standalone head or tail case-form.
/// </summary>
public enum CompositeVariantMode
{
    Legacy,
    FullPhraseOnly,
    FullPhrasePlusExceptions,
}

/// <summary>
/// Explicit, reviewed standalone exceptions. These are surface forms that are
/// independently attested as names/places, not generic parts of a composite.
/// Keep this list small and documented in OUTPUT_GENERATOR_POLICY.md.
/// </summary>
public static class CompositeGeneratorPolicy
{
    /// <summary>
    /// Deliberately Legacy until the generator change is activated after a
    /// fresh selfcheck/output comparison. The policy is implemented and tested
    /// opt-in; switching the production default is a separate decision.
    /// </summary>
    public const CompositeVariantMode DefaultMode = CompositeVariantMode.Legacy;

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> Approved =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            // 13/13 inspected contexts are the Leng region; the full form is
            // «Плато Ленг», but «Ленгом» is independently used.
            ["Плато Ленг"] = new HashSet<string>(StringComparer.Ordinal) { "Ленгом" },
            // Only the place forms are approved; «Цангом» in the inspected
            // sample is a person (Chogsum Tsang) and remains suppressed.
            ["Плато Цанг"] = new HashSet<string>(StringComparer.Ordinal) { "Цанга", "Цанге" },
        };

    public static IReadOnlyCollection<string> ExceptionsFor(string name) =>
        Approved.TryGetValue(name, out var values) ? values : Empty;

    private static readonly IReadOnlyCollection<string> Empty =
        new HashSet<string>(StringComparer.Ordinal);
}

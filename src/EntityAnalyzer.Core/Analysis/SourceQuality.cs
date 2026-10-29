using System.Text.RegularExpressions;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>
/// File-level source-quality detector for the known lexical-substitution
/// signature used by batch 35. This is intentionally only the reproducible
/// part of source policy B: six markers and density ≥ 1%. The 267 candidate
/// tail still needs the separate row-level sidecar and is not guessed here.
/// </summary>
public static class SourceQuality
{
    public const string Usable = "usable";
    public const string DegradedLexical = "degraded_lexical";
    public const string DegradedOcr = "degraded_ocr";
    public const string Unknown = "unknown";

    public const string ReasonLexicalSubstitution = "lexical_substitution";
    public const string ReasonOcrMissingSpace = "ocr_missing_space";
    public const string ReasonCandidateTail = "candidate_tail";
    public const string ReasonDetectorNegative = "detector_negative";
    public const string ReasonSidecarMissing = "sidecar_missing";

    /// <summary>
    /// The only source state that may enter trusted/strict views. `unknown`
    /// (old cache or an unassessed tail) and every `degraded_*` state stay in
    /// raw/review.
    /// </summary>
    public static bool IsUsable(string? quality) =>
        string.Equals(quality, Usable, StringComparison.Ordinal);

    private static readonly string[] Markers = ["dee", "kent", "irem", "ohio", "mu", "thran"];

    private static readonly Regex TokenRegex = new(
        @"[\p{L}\p{N}]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static SourceQualityResult Classify(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new(Unknown, "empty_source");

        var tokens = TokenRegex.Matches(text);
        if (tokens.Count == 0)
            return new(Unknown, "no_tokens");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var markerCount = 0;
        foreach (Match token in tokens)
        {
            var value = token.Value;
            if (!Markers.Contains(value, StringComparer.OrdinalIgnoreCase)) continue;
            markerCount++;
            seen.Add(value.ToLowerInvariant());
        }

        if (seen.Count == Markers.Length && (double)markerCount / tokens.Count >= 0.01)
            return new(DegradedLexical, ReasonLexicalSubstitution);

        return new(Usable, ReasonDetectorNegative);
    }
}

public readonly record struct SourceQualityResult(string Quality, string Reason);

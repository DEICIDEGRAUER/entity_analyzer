using EntityAnalyzer.Core.Semantics;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R1-8 «Песочница паттернов»: live-превью — вставленное предложение
/// сканируется включёнными паттернами, находки содержат позицию (Offset)
/// в нормализованном тексте для подчёркивания.
/// </summary>
[Collection("LogSequential")]
public class PatternSandboxTests
{
    private static (SemanticMatcher Matcher, IReadOnlyList<SemanticPattern> Patterns) MakeMatcher(
        params SemanticPattern[] extra)
    {
        var store = SemanticPatternStore.Load();
        var patterns = store.Patterns.Concat(extra).ToList();
        var matcher = new SemanticMatcher(patterns, store.Lexicons, ["Ктулху", "Наг и Йеб"]);
        return (matcher, patterns);
    }

    [Fact]
    public void Scan_returns_matches_with_nonnegative_offsets()
    {
        var (matcher, _) = MakeMatcher();
        var text = Text.TextNormalizer.Normalize("Жрец шептал у алтаря. Тьма отвечала.");
        var lineStarts = new[] { 0 };
        var matches = matcher.Scan(text, "sandbox", lineStarts);
        Assert.All(matches, m => Assert.True(m.Offset >= 0 && m.Offset < text.Length || m.Offset == 0));
    }

    [Fact]
    public void Literal_pattern_match_has_exact_offset()
    {
        // Минимальный паттерн с литералом: «жрец» перед любым словом.
        var pattern = new SemanticPattern(
            Id: "sb_test", Name: "Песочница-тест", Description: "",
            Tokens: [new SemanticToken(TokenKind.Literal, "жрец", Required: true)],
            Enabled: true);
        var lexicons = SemanticPatternStore.Load().Lexicons;
        var matcher = new SemanticMatcher([pattern], lexicons, []);

        var raw = "Старый жрец шептал.";
        var text = Text.TextNormalizer.Normalize(raw);
        var matches = matcher.Scan(text, "s", [0]);

        var m = Assert.Single(matches);
        Assert.Equal("sb_test", m.PatternId);
        var at = text[m.Offset..];
        Assert.True(at.StartsWith("жрец", StringComparison.OrdinalIgnoreCase),
            $"Offset={m.Offset} указывает не на «жрец»: [{at[..Math.Min(20, at.Length)]}]");
        Assert.Contains("жрец", m.Snippet, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Disabled_patterns_do_not_match()
    {
        var pattern = new SemanticPattern(
            Id: "sb_off", Name: "Выключен", Description: "",
            Tokens: [new SemanticToken(TokenKind.Literal, "жрец", Required: true)],
            Enabled: false);
        var lexicons = SemanticPatternStore.Load().Lexicons;
        var matcher = new SemanticMatcher([pattern], lexicons, []);
        var text = Text.TextNormalizer.Normalize("Жрец шептал.");
        Assert.Empty(matcher.Scan(text, "s", [0]));
    }
}

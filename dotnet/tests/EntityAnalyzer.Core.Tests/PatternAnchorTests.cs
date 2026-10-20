using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Semantics;
using Xunit;

namespace EntityAnalyzer.Tests;

/// <summary>
/// R1-4: компиляция семантических паттернов в якорный индекс.
/// Якорь = первый потребляющий токен паттерна, если он — обязательный
/// литерал (или any_of из литералов). Предложение без слов-якорей
/// не кандидатируется на паттерн вовсе; паттерны без якоря идут
/// легаси-путём. Контракт: результаты идентичны отключённой оптимизации.
/// </summary>
public class PatternAnchorTests
{
    private static SemanticPattern Pat(string id, params SemanticToken[] tokens) =>
        new(id, id, "", tokens);

    private static List<SemanticMatch> Run(SemanticMatcher m, string text) =>
        m.Scan(text, "f.txt", [0]);

    [Fact]
    public void LiteralFirst_Anchored()
    {
        var m = new SemanticMatcher([Pat("p1", SemanticToken.Lit("пробуждён"), SemanticToken.Prop())],
            new LexiconSet(), []);
        Assert.True(m.IsAnchored("p1"));
    }

    [Fact]
    public void CapitalizedFirst_LegacyPath()
    {
        var m = new SemanticMatcher([Pat("p2", SemanticToken.Cap(), SemanticToken.Lit("года"))],
            new LexiconSet(), []);
        Assert.False(m.IsAnchored("p2"));
    }

    [Fact]
    public void AnyOfLiterals_AnchorsOnEveryOption()
    {
        var m = new SemanticMatcher(
            [Pat("p3", SemanticToken.AnyOf(SemanticToken.Lit("час"), SemanticToken.Lit("год")), SemanticToken.Cap())],
            new LexiconSet(), []);
        Assert.True(m.IsAnchored("p3"));
    }

    [Fact]
    public void OptionalFirstToken_NotAnchorable()
    {
        // Первый потребляющий токен может быть пропущен (Required=false):
        // якорь по нему дал бы ложный негатив.
        var m = new SemanticMatcher(
            [Pat("p4", new SemanticToken(TokenKind.Literal, "час", Required: false), SemanticToken.Cap())],
            new LexiconSet(), []);
        Assert.False(m.IsAnchored("p4"));
    }

    [Fact]
    public void GuardBeforeLiteral_AnchorAfterGuards()
    {
        var m = new SemanticMatcher(
            [Pat("p5", SemanticToken.NotBefore(SemanticToken.Lit("не")), SemanticToken.Lit("пробуждён"))],
            new LexiconSet(), []);
        Assert.True(m.IsAnchored("p5"));
    }

    [Fact]
    public void LexiconFirst_Anchored()
    {
        var lexicons = new LexiconSet();
        lexicons.Add("kin", ["брат", "сестра"]);
        var m = new SemanticMatcher(
            [Pat("kin", SemanticToken.Lex("kin"), SemanticToken.Prop())], lexicons, []);
        Assert.True(m.IsAnchored("kin"));
    }

    [Fact]
    public void AnchoredRegexFirst_Anchored()
    {
        const string rx = @"^(18|19|20)\d{2}$|^\d{1,2}[./-]\d{1,2}[./-]\d{2,4}$";
        var m = new SemanticMatcher(
            [Pat("rx", SemanticToken.Rx(rx), SemanticToken.Cap())], new LexiconSet(), []);
        Assert.True(m.IsAnchored("rx"));
    }

    [Fact]
    public void UnanchoredRegexFirst_LegacyPath()
    {
        var m = new SemanticMatcher(
            [Pat("rx", SemanticToken.Rx(@"\d{4}"), SemanticToken.Cap())], new LexiconSet(), []);
        Assert.False(m.IsAnchored("rx"));
    }

    [Fact]
    public void EscapedHexRegexFirst_LegacyPath()
    {
        var m = new SemanticMatcher(
            [Pat("rx", SemanticToken.Rx(@"^\x41"), SemanticToken.Cap())], new LexiconSet(), []);
        Assert.False(m.IsAnchored("rx"));
    }

    [Fact]
    public void AnchoredRegex_Equals_Legacy()
    {
        const string rx = @"^(18|19|20)\d{2}$|^\d{1,2}[./-]\d{1,2}[./-]\d{2,4}$";
        var patterns = new List<SemanticPattern>
        {
            Pat("rx", SemanticToken.Rx(rx), SemanticToken.Cap()),
        };
        const string text = "В 1925 Году случилось. Это было 2024 Событие, а потом 42. " +
                            "Год 2020 Принёс перемены. Не 1919, а 2021 Год.";
        var legacy = Run(new SemanticMatcher(patterns, new LexiconSet(), [], 50, useAnchoring: false), text);
        var fast = Run(new SemanticMatcher(patterns, new LexiconSet(), [], 50, useAnchoring: true), text);
        Assert.Equal(
            legacy.Select(x => $"{x.PatternId}|{x.Line}|{x.Snippet}").ToList(),
            fast.Select(x => $"{x.PatternId}|{x.Line}|{x.Snippet}").ToList());
        Assert.NotEmpty(legacy);
    }

    /// <summary>Дифференциальный контракт: якорная оптимизация не меняет результаты.</summary>
    [Fact]
    public void Anchored_Equals_Legacy_OnMixedPatterns()
    {
        var patterns = new List<SemanticPattern>
        {
            Pat("lit", SemanticToken.Lit("пробуждение"), SemanticToken.Prop()),
            Pat("anyof", SemanticToken.AnyOf(SemanticToken.Lit("час"), SemanticToken.Lit("год")), SemanticToken.Cap()),
            Pat("cap", SemanticToken.Cap(), SemanticToken.Lit("луны")),
            Pat("opt", new SemanticToken(TokenKind.Literal, "час", Required: false), SemanticToken.Cap()),
            Pat("guard", SemanticToken.NotBefore(SemanticToken.Lit("не")), SemanticToken.Lit("спит"), SemanticToken.Prop()),
        };
        const string text = "Час Красной Луны настал. Пробуждение Ктулху близко. " +
                            "Год Воющей Собаки прошёл. Древний спит Ктулху вечно. " +
                            "Не спит Ктулху — он ждёт. Год Луны миновал.";
        var legacy = Run(new SemanticMatcher(patterns, new LexiconSet(), [], 50, useAnchoring: false), text);
        var fast = Run(new SemanticMatcher(patterns, new LexiconSet(), [], 50, useAnchoring: true), text);
        Assert.Equal(
            legacy.Select(x => $"{x.PatternId}|{x.Line}|{x.Snippet}").ToList(),
            fast.Select(x => $"{x.PatternId}|{x.Line}|{x.Snippet}").ToList());
        Assert.NotEmpty(legacy); // текст подобран так, что совпадения есть
    }
}

using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Codex;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Analysis;

public class TrustViewClassifierTests
{
    private static CodexDocument Doc(params Card[] cards)
    {
        var doc = new CodexDocument();
        foreach (var card in cards) doc.Cards.Add(card);
        return doc;
    }

    private static Card Card(string name, string canonical, params string[] aliases)
    {
        var card = new Card { Name = name, Canonical = canonical };
        card.Aliases.AddRange(aliases);
        foreach (var value in new[] { name, canonical })
            if (!string.IsNullOrWhiteSpace(value) && !card.Aliases.Contains(value))
                card.Aliases.Add(value);
        return card;
    }

    [Fact]
    public void Canonical_and_full_phrase_alias_are_trusted()
    {
        var doc = Doc(Card("Плато Ленг", "Plateau of Leng",
            "Leng", "The Plateau of Leng", "Plateau of Tsang"));

        var classifier = TrustViewClassifier.FromCodex(doc);
        var canonical = classifier.Assess("Плато Ленг", "Плато Ленг", "Плато Ленг — место.");
        var full = classifier.Assess("Плато Ленг", "The Plateau of Leng",
            "The Plateau of Leng is one of the locations.");

        Assert.True(canonical.IsTrusted);
        Assert.Equal(TrustViewLevel.Canonical, canonical.Level);
        Assert.True(full.IsTrusted);
        Assert.Equal(TrustViewLevel.StrongAlias, full.Level);
    }

    [Fact]
    public void Generic_title_with_anchor_is_still_weak()
    {
        var doc = Doc(Card("Йог-Сотот", "Yog-Sothoth", "The Gate", "The Key", "Aforgomon"));
        var classifier = TrustViewClassifier.FromCodex(doc);

        var gate = classifier.Assess("Йог-Сотот", "The Gate",
            "Yog-Sothoth is the Gate and the Key.");
        var wrongOwner = classifier.Assess("Йог-Сотот", "Aforgomon",
            "Yog-Sothoth is mentioned nearby.");

        Assert.Equal(TrustViewLevel.Weak, gate.Level);
        Assert.False(gate.IsTrusted);
        Assert.Equal(TrustViewLevel.Weak, wrongOwner.Level);
        Assert.False(wrongOwner.IsTrusted);
    }

    [Fact]
    public void Single_word_alias_needs_an_independent_anchor()
    {
        var doc = Doc(Card("Гуль", "Ghoul", "Гули", "Гулы"));
        var classifier = TrustViewClassifier.FromCodex(doc);

        var noMarker = classifier.Assess("Гуль", "Гули", "Гули — это нечисть.");
        var withMarker = classifier.Assess("Гуль", "Гули", "Гули, уже как Гулы (Ghoul), стали частью бестиария.");

        Assert.Equal(TrustViewLevel.StrongAlias, noMarker.Level);
        Assert.False(noMarker.IsTrusted);
        Assert.True(withMarker.IsTrusted);
    }

    [Fact]
    public void Pair_member_is_weak_but_full_pair_is_strong()
    {
        var doc = Doc(Card("Наг и Йеб", "Nug and Yeb", "Нуг", "Йеб", "Нуг и Йеб"));
        var classifier = TrustViewClassifier.FromCodex(doc);

        var member = classifier.Assess("Наг и Йеб", "Нуг", "Нуг и Йеб — близнецы.");
        var pair = classifier.Assess("Наг и Йеб", "Нуг и Йеб", "Нуг и Йеб — близнецы.");

        Assert.Equal(TrustViewLevel.Weak, member.Level);
        Assert.False(member.IsTrusted);
        Assert.Equal(TrustViewLevel.StrongAlias, pair.Level);
        Assert.True(pair.IsTrusted);
    }

    [Theory]
    [InlineData("Тчо-Тчо", "Tcho-Tcho", "Tcho-Tcho people", true)]
    [InlineData("Тчо-Тчо", "Tcho-Tcho", "Чо-Чо", false)]
    [InlineData("Книга Эйбона", "Book of Eibon", "The Book of Eibon", true)]
    public void Full_name_aliases_are_strong(string entity, string canonical, string variant, bool trusted)
    {
        var doc = Doc(Card(entity, canonical, variant));
        var classifier = TrustViewClassifier.FromCodex(doc);
        var result = classifier.Assess(entity, variant, variant);
        Assert.Equal(TrustViewLevel.StrongAlias, result.Level);
        Assert.Equal(trusted, result.IsTrusted);
    }

    [Fact]
    public void Short_homonym_is_not_promoted()
    {
        var doc = Doc(Card("Ляо", "Liao", "Лао"));
        var classifier = TrustViewClassifier.FromCodex(doc);
        var result = classifier.Assess("Ляо", "Лао", "Лао — древнекитайский философ.");
        Assert.Equal(TrustViewLevel.Weak, result.Level);
        Assert.False(result.IsTrusted);
    }

    [Fact]
    public void Shared_alias_is_not_trusted_even_with_marker()
    {
        var doc = Doc(
            Card("Плато Ленг", "Plateau of Leng", "Ленг"),
            Card("Монахи Ленга", "Monks of Leng", "Ленг"));
        var classifier = TrustViewClassifier.FromCodex(doc);

        var result = classifier.Assess("Плато Ленг", "Ленг", "Ленг — это плато; Плато Ленг рядом.");
        Assert.Equal(TrustViewLevel.Weak, result.Level);
        Assert.False(result.IsTrusted);
        Assert.Contains("shared", result.Reason);
    }

    [Fact]
    public void Missing_card_is_unknown()
    {
        var classifier = TrustViewClassifier.Empty;
        var result = classifier.Assess("Нет", "Нет", "контекст");
        Assert.Equal(TrustViewLevel.Unknown, result.Level);
        Assert.False(result.IsTrusted);
    }
}

public class SourceQualityTests
{
    [Fact]
    public void Six_markers_with_density_are_degraded()
    {
        var text = "dee kent irem ohio mu thran " + string.Join(' ', Enumerable.Repeat("слово", 50));
        var result = SourceQuality.Classify(text);
        Assert.Equal(SourceQuality.DegradedLexical, result.Quality);
        Assert.Equal(SourceQuality.ReasonLexicalSubstitution, result.Reason);
    }

    [Fact]
    public void Detector_negative_is_usable()
    {
        var result = SourceQuality.Classify("Обычный мифологический текст без подстановок.");
        Assert.Equal(SourceQuality.Usable, result.Quality);
        Assert.Equal(SourceQuality.ReasonDetectorNegative, result.Reason);
    }

    [Fact]
    public void Empty_text_is_unknown()
    {
        var result = SourceQuality.Classify("");
        Assert.Equal(SourceQuality.Unknown, result.Quality);
    }
}

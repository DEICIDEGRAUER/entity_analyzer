using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Variants;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Variants;

public class FullPhraseMorphologyTests
{
    [Fact]
    public void Phrase_keeps_head_and_tail_together()
    {
        var forms = FullPhraseMorphology.Decline("Книга Эйбона").ToList();

        Assert.Contains("Книге Эйбона", forms);
        Assert.Contains("Книгу Эйбона", forms);
        Assert.DoesNotContain("Книге", forms);
        Assert.DoesNotContain("Книгу", forms);
    }

    [Fact]
    public void Conjunction_declines_both_names()
    {
        var forms = FullPhraseMorphology.Decline("Наг и Йеб").ToList();

        Assert.Contains("Нага и Йеба", forms);
        Assert.Contains("Нагом и Йебом", forms);
        Assert.DoesNotContain("Нага", forms);
        Assert.DoesNotContain("Йеба", forms);
    }

    [Fact]
    public void Indeclinable_plateau_declines_only_tail()
    {
        var forms = FullPhraseMorphology.Decline("Плато Ленг").ToList();

        Assert.Contains("Плато Ленга", forms);
        Assert.Contains("Плато Ленгом", forms);
        Assert.DoesNotContain("Ленгом", forms);
    }

    [Fact]
    public void Adjective_agrees_with_neuter_noun()
    {
        var forms = FullPhraseMorphology.Decline("Черное Солнце").ToList();

        Assert.Contains("Черного Солнца", forms);
        Assert.Contains("Черному Солнцу", forms);
        Assert.DoesNotContain("Черное", forms);
    }

    [Fact]
    public void Plural_head_keeps_genitive_modifier()
    {
        var forms = FullPhraseMorphology.Decline("Монахи Ленга").ToList();

        Assert.Contains("Монахов Ленга", forms);
        Assert.Contains("Монахами Ленга", forms);
        Assert.DoesNotContain("Монахов", forms);
    }

    [Fact]
    public void Adjective_phrase_keeps_noun_together()
    {
        var forms = FullPhraseMorphology.Decline("Безымянный Туман").ToList();

        Assert.Contains("Безымянного Тумана", forms);
        Assert.Contains("Безымянному Туману", forms);
        Assert.DoesNotContain("Тумана", forms);
    }

    [Fact]
    public void Indeclinable_plateau_keeps_tsang_tail()
    {
        var forms = FullPhraseMorphology.Decline("Плато Цанг").ToList();

        Assert.Contains("Плато Цанга", forms);
        Assert.Contains("Плато Цангом", forms);
        Assert.DoesNotContain("Цанга", forms);
    }

    [Fact]
    public void Approved_tail_exception_is_not_a_general_generator()
    {
        var forms = new VariantFactory().Generate(
            "Плато Цанг",
            deepCorrupt: false,
            iterations: 1,
            compositeMode: CompositeVariantMode.FullPhrasePlusExceptions,
            standaloneExceptions: ["Цанга", "Цанге"]);

        Assert.Contains("Цанга", forms);
        Assert.Contains("Цанге", forms);
        Assert.DoesNotContain("Цангом", forms);
        Assert.DoesNotContain("Цангов", forms);
    }

    [Fact]
    public void Two_proper_names_decline_together()
    {
        var forms = FullPhraseMorphology.Decline("Куачиль Уттаус").ToList();

        Assert.Contains("Куачиля Уттауса", forms);
        Assert.Contains("Куачилем Уттаусом", forms);
        Assert.DoesNotContain("Куачиля", forms);
    }

    [Fact]
    public void Unsupported_or_non_russian_phrase_yields_no_bare_forms()
    {
        Assert.Empty(FullPhraseMorphology.Decline("Мири Нигри"));
        Assert.Empty(FullPhraseMorphology.Decline("Magnum Tenebrosum"));
        Assert.Empty(FullPhraseMorphology.Decline("Червь, грызущий в ночи"));
    }
}

public class CompositeVariantPolicyTests
{
    private readonly VariantFactory _factory = new();

    [Fact]
    public void Full_phrase_mode_does_not_emit_bare_book_forms()
    {
        var forms = _factory.Generate(
            "Книга Эйбона",
            deepCorrupt: false,
            iterations: 1,
            compositeMode: CompositeVariantMode.FullPhraseOnly);

        Assert.Contains("Книге Эйбона", forms);
        Assert.DoesNotContain("Книге", forms);
        Assert.DoesNotContain("Книгах", forms);
        Assert.DoesNotContain("Книгой", forms);
        Assert.DoesNotContain("Книгами", forms);
    }

    [Fact]
    public void Full_phrase_mode_does_not_emit_pair_members()
    {
        var forms = _factory.Generate(
            "Наг и Йеб",
            deepCorrupt: false,
            iterations: 1,
            compositeMode: CompositeVariantMode.FullPhraseOnly);

        Assert.Contains("Нага и Йеба", forms);
        Assert.DoesNotContain("Нага", forms);
        Assert.DoesNotContain("Йеба", forms);
    }

    [Fact]
    public void Approved_exception_is_emitted_only_in_plus_exceptions_mode()
    {
        var forms = _factory.Generate(
            "Плато Ленг",
            deepCorrupt: false,
            iterations: 1,
            compositeMode: CompositeVariantMode.FullPhrasePlusExceptions,
            standaloneExceptions: new HashSet<string>(StringComparer.Ordinal) { "Ленгом" });

        Assert.Contains("Плато Ленгом", forms);
        Assert.Contains("Ленгом", forms);
        Assert.DoesNotContain("Ленга", forms);
    }

    [Fact]
    public void Legacy_still_reproduces_standalone_forms()
    {
        var forms = _factory.Generate(
            "Наг и Йеб",
            deepCorrupt: false,
            iterations: 1,
            compositeMode: CompositeVariantMode.Legacy);

        Assert.Contains("Нага", forms);
    }

    [Fact]
    public void Entity_catalog_uses_full_phrase_policy_when_configured()
    {
        var definition = new EntityDefinition(
            "Наг и Йеб",
            "Nug and Yeb",
            ["Наг и Йеб", "Nug and Yeb"],
            [])
        {
            CompositeMode = CompositeVariantMode.FullPhrasePlusExceptions,
        };
        var catalog = new EntityCatalog([definition]);
        var variants = catalog.VariantsByEntity["Наг и Йеб"];

        Assert.Contains("Нага и Йеба", variants);
        Assert.DoesNotContain("Нага", variants);
        Assert.DoesNotContain("Йеба", variants);
    }

    [Fact]
    public void Codex_adapter_can_opt_into_full_phrase_policy()
    {
        var doc = new CodexDocument();
        var card = Card.Create("deity", "Наг и Йеб");
        card.Canonical = "Nug and Yeb";
        doc.Cards.Add(card);

        var definition = CodexSearchAdapter
            .ToDefinitions(doc, CompositeVariantMode.FullPhrasePlusExceptions)
            .Single();
        var catalog = new EntityCatalog([definition]);
        var variants = catalog.VariantsByEntity["Наг и Йеб"];

        Assert.Equal(CompositeVariantMode.FullPhrasePlusExceptions, definition.CompositeMode);
        Assert.Contains("Нага и Йеба", variants);
        Assert.DoesNotContain("Нага", variants);
    }
}

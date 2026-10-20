using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Genealogy;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Genealogy;

/// <summary>
/// Группа 4 (item 5): разбор GEDCOM, авточитаемое описание, семейное дерево,
/// поколения и раскладка больших переплетённых деревьев.
/// </summary>
public class GenealogyTests
{
    private const string Sample = """
        0 HEAD
        1 CHAR UTF-8
        0 @I1@ INDI
        1 NAME Иван /Смирнов/
        1 SEX M
        1 BIRT
        2 DATE 12 JAN 1900
        2 PLAC Москва
        1 DEAT
        2 DATE 1972
        1 FAMS @F1@
        1 NOTE Долгая заметка
        2 CONT продолжение заметки
        0 @I2@ INDI
        1 NAME Мария /Петрова/
        1 SEX F
        1 FAMS @F1@
        0 @I3@ INDI
        1 NAME Пётр /Смирнов/
        1 FAMC @F1@
        1 BIRT
        2 DATE 1925
        0 @F1@ FAM
        1 HUSB @I1@
        1 WIFE @I2@
        1 CHIL @I3@
        1 MARR
        2 DATE 1920
        2 PLAC Казань
        0 TRLR
        """;

    [Fact]
    public void Gedcom_parses_people_families_events_and_continuations()
    {
        var doc = GedcomParser.Parse(Sample);

        Assert.Equal(3, doc.Persons.Count);
        var ivan = doc.Person("I1")!;
        Assert.Equal("Иван Смирнов", ivan.Name);
        Assert.Equal("Иван", ivan.Given);
        Assert.Equal("Смирнов", ivan.Surname);
        Assert.Equal("M", ivan.Sex);
        Assert.Equal(1900, ivan.Birth!.Year);
        Assert.Equal(1, ivan.Birth.Month);
        Assert.Equal(12, ivan.Birth.Day);
        Assert.Equal("Москва", ivan.BirthPlace);
        Assert.Equal(1972, ivan.Death!.Year);
        Assert.Equal("Долгая заметка\nпродолжение заметки", ivan.Note);
        Assert.Contains("F1", ivan.SpouseOf);

        var family = doc.Family("F1")!;
        Assert.Equal("I1", family.HusbandId);
        Assert.Equal("I2", family.WifeId);
        Assert.Contains("I3", family.Children);
        Assert.Equal(1920, family.Marriage!.Year);
        Assert.Equal("Казань", family.MarriagePlace);
        Assert.Empty(doc.Warnings);
    }

    [Fact]
    public void Gedcom_warns_about_dangling_family_references()
    {
        var doc = GedcomParser.Parse("""
            0 @I1@ INDI
            1 NAME Один /Тестов/
            1 FAMC @F404@
            0 TRLR
            """);

        Assert.Contains(doc.Warnings, w => w.Contains("F404"));
    }

    [Fact]
    public void Gedcom_skips_duplicate_records_and_keeps_indexes_usable()
    {
        var doc = GedcomParser.Parse("""
            0 @I1@ INDI
            1 NAME Первый /Тестов/
            0 @I1@ INDI
            1 NAME Второй /Тестов/
            0 TRLR
            """);

        Assert.Single(doc.Persons);
        Assert.Equal("Первый Тестов", doc.Person("I1")!.Name);
        Assert.Contains(doc.Warnings, w => w.Contains("I1") && w.Contains("повтор"));
    }

    [Theory]
    [InlineData("12 JAN 1900", "12 января 1900", 1900, 1, 12)]
    [InlineData("ABT 1900", "ок. 1900", 1900, null, null)]
    [InlineData("BEF 1912", "до 1912", 1912, null, null)]
    [InlineData("AFT 1912", "после 1912", 1912, null, null)]
    [InlineData("1900", "1900", 1900, null, null)]
    [InlineData("12.01.1900", "12 января 1900", 1900, 1, 12)]
    [InlineData("МАЙ 1910", "май 1910", 1910, 5, null)]
    [InlineData("45 до н. э.", "45 до н. э.", -45, null, null)]
    public void Gedcom_dates_become_russian_text(string raw, string display, int year, int? month, int? day)
    {
        var date = GedcomDateParser.Parse(raw)!;

        Assert.Equal(display, date.Display);
        Assert.Equal(year, date.Year);
        Assert.Equal(month, date.Month);
        Assert.Equal(day, date.Day);
    }

    [Fact]
    public void Gedcom_date_range_is_readable()
    {
        var date = GedcomDateParser.Parse("BET 1900 AND 1910")!;

        Assert.Equal("между 1900 и 1910", date.Display);
        Assert.Equal(1900, date.Year);
        Assert.Equal("BET", date.Qualifier);
    }

    [Fact]
    public void Narrator_builds_summary_outline_cards_and_warnings()
    {
        var doc = GedcomParser.Parse(Sample);

        var summary = GedcomNarrator.Summary(doc);
        Assert.Contains("Людей: 3", summary);
        Assert.Contains("семей: 1", summary);

        var outline = GedcomNarrator.Outline(doc);
        Assert.Contains("Иван Смирнов (1900—1972)", outline);
        Assert.Contains("∞ Мария Петрова", outline);
        Assert.Contains("    • Пётр Смирнов (1925—)", outline);

        var card = GedcomNarrator.PersonCard(doc, doc.Person("I1")!);
        Assert.Contains("рождение: 12 января 1900, Москва", card);
        Assert.Contains("брак: Мария Петрова (1920, Казань)", card);
        Assert.Contains("дети: Пётр Смирнов", card);

        var text = GedcomNarrator.ToReadableText(doc);
        Assert.Contains("ЧИТАЕМОЕ ОПИСАНИЕ", text);
        Assert.Contains("РОДОВОЙ ОБЗОР", text);
        Assert.Contains("КАРТОЧКИ", text);
    }

    [Fact]
    public void Family_tree_from_gedcom_assigns_generations_and_spouse_links()
    {
        var tree = FamilyTreeBuilder.FromGedcom(GedcomParser.Parse(Sample));

        Assert.Equal(3, tree.Nodes.Count);
        Assert.Equal(2, tree.ParentLinks.Count);
        Assert.Single(tree.SpouseLinks);
        Assert.Equal(2, tree.GenerationCount);

        var ivan = tree.Nodes.Single(n => n.Id == "I1");
        var maria = tree.Nodes.Single(n => n.Id == "I2");
        var peter = tree.Nodes.Single(n => n.Id == "I3");
        Assert.Equal(0, ivan.Generation);
        Assert.Equal(0, maria.Generation);
        Assert.Equal(1, peter.Generation);
        Assert.Contains(tree.ParentLinks, l => l.ParentId == "I1" && l.ChildId == "I3" && l.Label == "отец");
        Assert.Contains(tree.ParentLinks, l => l.ParentId == "I2" && l.ChildId == "I3" && l.Label == "мать");
    }

    [Fact]
    public void Layout_keeps_spouses_together_and_rows_by_generation()
    {
        var tree = FamilyTreeBuilder.FromGedcom(GedcomParser.Parse(Sample));
        var layout = FamilyTreeLayout.Calculate(tree);
        var placements = layout.Placements.ToDictionary(p => p.Id, StringComparer.Ordinal);

        Assert.Equal(placements["I1"].Y, placements["I2"].Y);
        Assert.Equal(180, Math.Abs(placements["I1"].X - placements["I2"].X), 3);
        Assert.True(placements["I3"].Y > placements["I1"].Y);
        Assert.True(layout.Width > 0);
        Assert.Equal(2, layout.GenerationCount);
    }

    [Fact]
    public void Family_tree_from_codex_uses_only_spawn_and_parent_links()
    {
        var doc = CodexDocument.CreateEmpty();
        var az = Card.Create("deity", "Азатот");
        var nug = Card.Create("deity", "Наг и Йеб");
        var cthulhu = Card.Create("deity", "Ктулху");
        var unrelated = Card.Create("deity", "Баст");
        doc.Cards.Add(az);
        doc.Cards.Add(nug);
        doc.Cards.Add(cthulhu);
        doc.Cards.Add(unrelated);
        doc.Links.Add(new CardLink(az.Id, nug.Id, "spawn"));
        doc.Links.Add(new CardLink(nug.Id, cthulhu.Id, "parent"));
        doc.Links.Add(new CardLink(az.Id, unrelated.Id, "cooccur"));

        var tree = FamilyTreeBuilder.FromCodex(doc);

        Assert.Equal(3, tree.Nodes.Count);
        Assert.Equal(2, tree.ParentLinks.Count);
        Assert.DoesNotContain(tree.Nodes, n => n.Name == "Баст");
        var generations = tree.Nodes.ToDictionary(n => n.Name, n => n.Generation);
        Assert.Equal(0, generations["Азатот"]);
        Assert.Equal(1, generations["Наг и Йеб"]);
        Assert.Equal(2, generations["Ктулху"]);
    }

    [Fact]
    public void Gedcom_parses_alternate_names_pedigree_and_quality()
    {
        var doc = GedcomParser.Parse("""
            0 @I1@ INDI
            1 NAME Иван /Смирнов/
            1 NAME Джон /Смит/
            1 SEX M
            1 FAMC @F1@
            2 PEDI adopted
            2 QUAY 2
            0 @I2@ INDI
            1 NAME Приёмный /Отец/
            0 @I3@ INDI
            1 NAME Приёмная /Мать/
            0 @F1@ FAM
            1 HUSB @I2@
            1 WIFE @I3@
            1 CHIL @I1@
            0 TRLR
            """);

        var person = doc.Person("I1")!;
        Assert.Equal("Иван Смирнов", person.Name);
        Assert.Single(person.AlternateNames);
        Assert.Equal("Джон Смит", person.AlternateNames[0]);
        Assert.Equal("adopted", person.ChildPedigree["F1"]);
        Assert.Equal("2", person.ChildConfidence["F1"]);

        var tree = FamilyTreeBuilder.FromGedcom(doc);
        Assert.All(tree.ParentLinks, link =>
        {
            Assert.Equal(FamilyRelationKind.Adoptive, link.Kind);
            Assert.Equal(FamilyConfidence.Likely, link.Confidence);
        });
        Assert.Contains("приёмн", tree.ParentLinks[0].DisplayLabel);
        Assert.Contains("другие имена: Джон Смит", GedcomNarrator.PersonCard(doc, person));
    }

    [Fact]
    public void Family_tree_from_codex_supports_typed_relations_and_link_confidence()
    {
        var doc = CodexDocument.CreateEmpty();
        var parent = Card.Create("person", "Опекун");
        var child = Card.Create("person", "Подопечная");
        var partner = Card.Create("person", "Партнёр");
        doc.Cards.Add(parent);
        doc.Cards.Add(child);
        doc.Cards.Add(partner);
        doc.Relations.Add(new RelationType("custom_guardian", "опекун", "подопечный"));
        doc.Links.Add(new CardLink(parent.Id, child.Id, "custom_guardian", "",
            FamilyConfidenceScale.Id(FamilyConfidence.Disputed)));
        doc.Links.Add(new CardLink(parent.Id, partner.Id, "partner", "",
            FamilyConfidenceScale.Id(FamilyConfidence.Hypothesis)));

        var tree = FamilyTreeBuilder.FromCodex(doc);

        Assert.Equal(3, tree.Nodes.Count);
        var link = Assert.Single(tree.ParentLinks);
        Assert.Equal(FamilyRelationKind.Guardian, link.Kind);
        Assert.Equal(FamilyConfidence.Disputed, link.Confidence);
        Assert.Contains("опека", link.DisplayLabel);
        var pair = Assert.Single(tree.SpouseLinks);
        Assert.Equal(FamilyRelationKind.Partner, pair.Kind);
        Assert.Equal(FamilyConfidence.Hypothesis, pair.Confidence);
        Assert.Equal(FamilyConfidence.Disputed, tree.Nodes.Single(n => n.Id == parent.Id).Confidence);
        Assert.Equal(1, tree.HypothesisCount);
    }

    [Fact]
    public void Family_tree_keeps_distinct_relation_kinds_between_same_pair()
    {
        var doc = CodexDocument.CreateEmpty();
        var parent = Card.Create("person", "Родитель");
        var child = Card.Create("person", "Ребёнок");
        doc.Cards.Add(parent);
        doc.Cards.Add(child);
        doc.Links.Add(new CardLink(parent.Id, child.Id, "parent", "", "proven"));
        doc.Links.Add(new CardLink(parent.Id, child.Id, "adopt", "", "hypothesis"));

        var tree = FamilyTreeBuilder.FromCodex(doc);

        Assert.Equal(2, tree.ParentLinks.Count);
        Assert.Contains(tree.ParentLinks, l => l.Kind == FamilyRelationKind.Biological);
        Assert.Contains(tree.ParentLinks, l => l.Kind == FamilyRelationKind.Adoptive);
    }

    [Fact]
    public void Family_tree_places_partners_in_one_generation_and_adjacent_columns()
    {
        var doc = CodexDocument.CreateEmpty();
        var parent = Card.Create("person", "Родитель");
        var child = Card.Create("person", "Ребёнок");
        var partner = Card.Create("person", "Партнёр без родителей");
        doc.Cards.Add(parent);
        doc.Cards.Add(child);
        doc.Cards.Add(partner);
        doc.Links.Add(new CardLink(parent.Id, child.Id, "parent"));
        doc.Links.Add(new CardLink(child.Id, partner.Id, "partner"));

        var tree = FamilyTreeBuilder.FromCodex(doc);
        var childNode = tree.Nodes.Single(n => n.Id == child.Id);
        var partnerNode = tree.Nodes.Single(n => n.Id == partner.Id);
        Assert.Equal(1, childNode.Generation);
        Assert.Equal(childNode.Generation, partnerNode.Generation);

        var layout = FamilyTreeLayout.Calculate(tree);
        var childX = layout.Placements.Single(p => p.Id == child.Id).X;
        var partnerX = layout.Placements.Single(p => p.Id == partner.Id).X;
        Assert.Equal(180, Math.Abs(partnerX - childX));
    }

    [Fact]
    public void Family_tree_subset_around_builds_ego_network_and_keeps_original()
    {
        var doc = CodexDocument.CreateEmpty();
        var a = Card.Create("person", "А");
        var b = Card.Create("person", "Б");
        var c = Card.Create("person", "В");
        var d = Card.Create("person", "Г");
        var e = Card.Create("person", "Д");
        foreach (var card in new[] { a, b, c, d, e }) doc.Cards.Add(card);
        doc.Links.Add(new CardLink(a.Id, b.Id, "parent"));
        doc.Links.Add(new CardLink(b.Id, c.Id, "parent"));
        doc.Links.Add(new CardLink(c.Id, d.Id, "parent"));
        doc.Links.Add(new CardLink(b.Id, e.Id, "partner"));

        var tree = FamilyTreeBuilder.FromCodex(doc);
        var ego = tree.SubsetAround(b.Id, 1);

        Assert.Equal(4, ego.Nodes.Count);
        Assert.DoesNotContain(ego.Nodes, n => n.Id == d.Id);
        Assert.Equal(2, ego.ParentLinks.Count);
        Assert.Single(ego.SpouseLinks);
        Assert.Equal(5, tree.Nodes.Count);
        Assert.Equal(3, tree.ParentLinks.Count);

        var deep = tree.SubsetAround(c.Id, 1);
        Assert.Equal(0, deep.Nodes.Single(n => n.Id == b.Id).Generation);
        Assert.Equal(1, deep.Nodes.Single(n => n.Id == c.Id).Generation);
        Assert.Equal(2, deep.Nodes.Single(n => n.Id == d.Id).Generation);
    }

    [Fact]
    public void Family_tree_hides_hypothesis_links_without_mutating_source()
    {
        var doc = CodexDocument.CreateEmpty();
        var a = Card.Create("person", "А");
        var b = Card.Create("person", "Б");
        var c = Card.Create("person", "В");
        var d = Card.Create("person", "Г");
        foreach (var card in new[] { a, b, c, d }) doc.Cards.Add(card);
        doc.Links.Add(new CardLink(a.Id, b.Id, "parent", "",
            FamilyConfidenceScale.Id(FamilyConfidence.Proven)));
        doc.Links.Add(new CardLink(b.Id, c.Id, "parent", "",
            FamilyConfidenceScale.Id(FamilyConfidence.Hypothesis)));
        doc.Links.Add(new CardLink(c.Id, d.Id, "parent", "",
            FamilyConfidenceScale.Id(FamilyConfidence.Proven)));

        var tree = FamilyTreeBuilder.FromCodex(doc);
        var factual = tree.WithoutHypotheses();

        Assert.Equal(4, tree.Nodes.Count);
        Assert.Equal(3, tree.ParentLinks.Count);
        Assert.Equal(2, factual.ParentLinks.Count);
        Assert.Equal(4, factual.Nodes.Count);
        Assert.DoesNotContain(factual.ParentLinks, l => l.ChildId == c.Id && l.ParentId == b.Id);
        Assert.Equal(1, tree.HypothesisCount);
        Assert.Equal(0, factual.HypothesisCount);
    }

    [Fact]
    public void Family_tree_audit_finds_isolates_duplicate_names_and_multiple_parents()
    {
        var doc = GedcomParser.Parse("""
            0 @I1@ INDI
            1 NAME Иван /Смирнов/
            0 @I2@ INDI
            1 NAME Иван /Смирнов/
            0 @I3@ INDI
            1 NAME Третий /Родитель/
            0 @I4@ INDI
            1 NAME Ребёнок /Тестов/
            1 FAMC @F1@
            1 FAMC @F2@
            0 @I5@ INDI
            1 NAME Одинокий /Тестов/
            0 @F1@ FAM
            1 HUSB @I1@
            1 WIFE @I2@
            1 CHIL @I4@
            0 @F2@ FAM
            1 HUSB @I3@
            1 CHIL @I4@
            0 TRLR
            """);

        var issues = FamilyTreeAudit.Analyze(FamilyTreeBuilder.FromGedcom(doc));

        Assert.Contains(issues, i => i.Kind == FamilyIssueKind.Isolated &&
            i.NodeIds.Contains("I5"));
        Assert.Contains(issues, i => i.Kind == FamilyIssueKind.DuplicateName &&
            i.NodeIds.Contains("I1") && i.NodeIds.Contains("I2"));
        Assert.Contains(issues, i => i.Kind == FamilyIssueKind.MultipleParents &&
            i.NodeIds.Contains("I4"));
    }

    [Fact]
    public void Family_tree_html_exposes_types_confidence_and_hypothesis_toggle()
    {
        var doc = CodexDocument.CreateEmpty();
        var a = Card.Create("person", "А");
        var b = Card.Create("person", "Б");
        doc.Cards.Add(a);
        doc.Cards.Add(b);
        doc.Links.Add(new CardLink(a.Id, b.Id, "adopt", "",
            FamilyConfidenceScale.Id(FamilyConfidence.Hypothesis)));

        var tree = FamilyTreeBuilder.FromCodex(doc);
        var html = FamilyTreeHtml.Render(tree, "текст", "Тип <связи>");

        Assert.Contains("приёмн", html);
        Assert.Contains("гипотеза", html);
        Assert.Contains("id=\"hyp\"", html);
        Assert.Contains("hide-hyp", html);
        Assert.Contains("edge-label", html);
        Assert.Contains("Тип &lt;связи&gt;", html);
    }

    [Fact]
    public void Family_tree_survives_cycles()
    {
        var doc = CodexDocument.CreateEmpty();
        var a = Card.Create("deity", "А");
        var b = Card.Create("deity", "Б");
        doc.Cards.Add(a);
        doc.Cards.Add(b);
        doc.Links.Add(new CardLink(a.Id, b.Id, "spawn"));
        doc.Links.Add(new CardLink(b.Id, a.Id, "spawn"));

        var tree = FamilyTreeBuilder.FromCodex(doc);

        Assert.Equal(2, tree.Nodes.Count);
        Assert.All(tree.Nodes, n => Assert.InRange(n.Generation, 0, 1));
    }

    [Fact]
    public void Family_tree_subset_keeps_one_component_and_its_links()
    {
        var doc = CodexDocument.CreateEmpty();
        var a = Card.Create("deity", "А");
        var b = Card.Create("deity", "Б");
        var c = Card.Create("deity", "В");
        var d = Card.Create("deity", "Г");
        doc.Cards.Add(a);
        doc.Cards.Add(b);
        doc.Cards.Add(c);
        doc.Cards.Add(d);
        doc.Links.Add(new CardLink(a.Id, b.Id, "spawn"));
        doc.Links.Add(new CardLink(c.Id, d.Id, "parent"));

        var tree = FamilyTreeBuilder.FromCodex(doc);
        Assert.Equal(2, tree.Nodes.Select(n => n.Component).Distinct().Count());

        var component = tree.Nodes.Single(n => n.Id == a.Id).Component;
        var subset = tree.Subset(component);

        Assert.Equal(2, subset.Nodes.Count);
        Assert.All(subset.Nodes, n => Assert.Equal(component, n.Component));
        Assert.Single(subset.ParentLinks);
        Assert.Equal(a.Id, subset.ParentLinks[0].ParentId);
        Assert.Equal(b.Id, subset.ParentLinks[0].ChildId);
        Assert.Equal(2, subset.GenerationCount);
        var other = tree.Subset(tree.Nodes.Single(n => n.Id == c.Id).Component);
        Assert.Equal(tree.Nodes.Count, subset.Nodes.Count + other.Nodes.Count);
    }

    [Fact]
    public void Family_tree_html_is_self_contained_and_escapes_names()
    {
        var tree = FamilyTreeBuilder.FromGedcom(GedcomParser.Parse(Sample));
        var html = FamilyTreeHtml.Render(tree, GedcomNarrator.ToReadableText(GedcomParser.Parse(Sample)), "Тест <древа>");

        Assert.Contains("Тест &lt;древа&gt;", html);
        Assert.Contains("Иван Смирнов", html);
        Assert.Contains("<svg id=\"graph\">", html);
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("Читаемый текст", html);
        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("d3js.org", html);
    }
}

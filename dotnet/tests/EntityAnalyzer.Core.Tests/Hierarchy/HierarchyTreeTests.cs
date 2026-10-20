using System.Text.Json;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Hierarchy;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Hierarchy;

/// <summary>
/// Группа 4 («Древо Иерархии»): автосборка дерева из типизированных связей
/// кодекса, разрыв циклов ссылками, экспорт Markdown/JSON.
/// </summary>
public class HierarchyTreeTests
{
    private static CodexDocument Doc()
    {
        return CodexDocument.CreateEmpty();
    }

    private static Card Add(CodexDocument doc, string type, string name)
    {
        var card = Card.Create(type, name);
        doc.Cards.Add(card);
        return card;
    }

    [Fact]
    public void Hierarchy_builds_from_serves_spawn_and_located()
    {
        var doc = Doc();
        var azathoth = Add(doc, "deity", "Азатот");
        var nug = Add(doc, "deity", "Наг и Йеб");
        var cthulhu = Add(doc, "deity", "Ктулху");
        var rlyeh = Add(doc, "location", "Р'льех");
        doc.Links.Add(new CardLink(nug.Id, azathoth.Id, "serves"));      // Наг служит Азатоту
        doc.Links.Add(new CardLink(azathoth.Id, cthulhu.Id, "spawn"));   // Азатот породил Ктулху
        doc.Links.Add(new CardLink(cthulhu.Id, rlyeh.Id, "located"));    // Ктулху находится в Р'льехе
        doc.Links.Add(new CardLink(nug.Id, cthulhu.Id, "ally"));         // союз — не иерархия

        var tree = HierarchyTreeBuilder.Build(doc, ["serves", "spawn"]);

        Assert.Single(tree.Roots);
        var root = tree.Roots[0];
        Assert.Equal("Азатот", root.Title);
        Assert.Equal("Божество", root.Kind);
        Assert.Equal(3, tree.Cards);
        Assert.Contains(root.Children, n => n.Title == "Наг и Йеб");
        var cthulhuNode = root.Children.Single(n => n.Title == "Ктулху");
        Assert.Empty(cthulhuNode.Children);

        // «находится в» разворачивает вложенность: место — родитель, объект — ребёнок.
        var places = HierarchyTreeBuilder.Build(doc, ["located"]);
        Assert.Single(places.Roots);
        Assert.Equal("Р'льех", places.Roots[0].Title);
        Assert.Equal("Ктулху", places.Roots[0].Children[0].Title);
    }

    [Fact]
    public void Hierarchy_marks_repeated_cards_as_references()
    {
        var doc = Doc();
        var a = Add(doc, "deity", "Азатот");
        var b = Add(doc, "deity", "Наг и Йеб");
        var c = Add(doc, "deity", "Ктулху");
        doc.Links.Add(new CardLink(a.Id, c.Id, "spawn"));
        doc.Links.Add(new CardLink(b.Id, c.Id, "spawn"));

        var tree = HierarchyTreeBuilder.Build(doc);

        Assert.Equal(2, tree.Roots.Count);
        var cNodes = tree.Flatten().Where(n => n.Title == "Ктулху").ToList();
        Assert.Equal(2, cNodes.Count);
        Assert.Single(cNodes, n => !n.IsReference);
        Assert.Single(cNodes, n => n.IsReference);
        Assert.Equal(1, tree.References);
    }

    [Fact]
    public void Hierarchy_survives_cycles_and_respects_root_choice()
    {
        var doc = Doc();
        var a = Add(doc, "deity", "А");
        var b = Add(doc, "deity", "Б");
        doc.Links.Add(new CardLink(a.Id, b.Id, "spawn"));
        doc.Links.Add(new CardLink(b.Id, a.Id, "spawn"));

        var tree = HierarchyTreeBuilder.Build(doc, rootId: b.Id);

        Assert.Single(tree.Roots);
        Assert.Equal("Б", tree.Roots[0].Title);
        Assert.Equal("А", tree.Roots[0].Children[0].Title);
        Assert.True(tree.Roots[0].Children[0].Children[0].IsReference);
        Assert.True(tree.Placements >= 3);
    }

    [Fact]
    public void Hierarchy_exports_markdown_and_json()
    {
        var doc = Doc();
        var azathoth = Add(doc, "deity", "Азатот");
        var cthulhu = Add(doc, "deity", "Ктулху");
        doc.Links.Add(new CardLink(azathoth.Id, cthulhu.Id, "spawn"));
        var tree = HierarchyTreeBuilder.Build(doc);

        var markdown = HierarchyTreeBuilder.ToMarkdown(tree);
        Assert.Contains("# Древо Иерархии", markdown);
        Assert.Contains("- Азатот (Божество)", markdown);
        Assert.Contains("  - Ктулху (Божество)", markdown);

        var json = HierarchyTreeBuilder.ToJson(tree);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(1, parsed.RootElement.GetProperty("roots").GetArrayLength());
        Assert.Equal("Азатот", parsed.RootElement.GetProperty("roots")[0].GetProperty("title").GetString());
    }

    [Fact]
    public void Hierarchy_ignores_unknown_relations_and_cards()
    {
        var doc = Doc();
        var a = Add(doc, "deity", "А");
        doc.Links.Add(new CardLink(a.Id, "нет-такой", "spawn"));

        var tree = HierarchyTreeBuilder.Build(doc);

        Assert.Empty(tree.Roots);
        Assert.Equal(0, tree.Cards);
    }
}

using System.Text;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Exporting;
using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Variants;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// Codex (universal cards) contracts: model, service, export/import, search adapter.
/// </summary>
public sealed class CodexTests
{
    // ─────────────────────────── helpers ───────────────────────────

    private static CardType Deity() => new("deity", "Божество", "🜏", "#C9A227");
    private static CardType City() => new("city", "Город", "🏛", "#7FB2D9");
    private static CardType Artifact() => new("artifact", "Артефакт", "⚱", "#B07FD9");

    private static RelationType Owns() => new("owns", "владеет", "принадлежит");
    private static RelationType Located() => new("located", "находится в", "содержит");

    private static Card MakeCard(string type, string name, params string[] aliases)
    {
        var c = Card.Create(type, name);
        foreach (var a in aliases) ((List<string>)c.Aliases).Add(a);
        return c;
    }

    private static CodexDocument SampleDoc()
    {
        var doc = CodexDocument.CreateEmpty();
        doc.Types.Add(Deity());
        doc.Types.Add(City());
        doc.Types.Add(Artifact());
        doc.Relations.Add(Owns());
        doc.Relations.Add(Located());

        var az = MakeCard("deity", "Азатот", "Azathot");
        var nug = MakeCard("deity", "Наг и Йеб");
        var pp = MakeCard("city", "Пнакот");
        var stone = MakeCard("artifact", "Камень Пнакота");

        doc.Cards.Add(az);
        doc.Cards.Add(nug);
        doc.Cards.Add(pp);
        doc.Cards.Add(stone);

        doc.Links.Add(new CardLink(stone.Id, pp.Id, "located"));
        doc.Links.Add(new CardLink(nug.Id, az.Id, "owns"));
        return doc;
    }

    // ─────────────────────── A. модель карточек ───────────────────────

    [Fact]
    public void FieldSchema_type_kinds_cover_all_declared_types()
    {
        var kinds = Enum.GetValues<FieldType>();
        Assert.True(kinds.Length >= 8, "ожидается ≥8 типов полей");
        Assert.Contains(FieldType.Link, kinds);
        Assert.Contains(FieldType.Choice, kinds);
    }

    [Fact]
    public void FieldValue_formats_each_kind_readably()
    {
        Assert.Equal("Привет", FieldValue.FromText("Привет").Display());
        Assert.Equal("42", FieldValue.FromNumber(42).Display());
        Assert.Equal("да", FieldValue.FromFlag(true).Display());
        Assert.Equal("нет", FieldValue.FromFlag(false).Display());
        Assert.Equal("Антарктика", FieldValue.FromChoice("Антарктика").Display());
        Assert.Equal("→ Камень Пнакота", FieldValue.FromLink("id123", "Камень Пнакота").Display());
        Assert.Equal("a, b, c", FieldValue.FromList(["a", "b", "c"]).Display());
        Assert.Equal("", FieldValue.Empty.Display());
    }

    [Fact]
    public void Card_search_text_includes_name_aliases_tags_and_fields()
    {
        var c = MakeCard("city", "Пнакот", "Pnakotus");
        c.Tags.Add("городов древних");
        c.Fields["f1"] = FieldValue.FromText("Антарктика");
        var text = CodexService.SearchText(c);
        Assert.Contains("пнакот", text);
        Assert.Contains("pnakotus", text);
        Assert.Contains("городов древних", text);
        Assert.Contains("антарктика", text);
    }

    [Fact]
    public void Migration_from_v1_catalog_preserves_entities_groups_and_enabled()
    {
        var v1 = new CatalogDocument(1,
        [
            new EntityDefinition("Азатот", "Azathoth", ["азатот"], ["Султан демонов"], Enabled: true, Group: "Внешние"),
            new EntityDefinition("Старец", "Elder", [], [], Enabled: false, Group: ""),
        ]);
        var doc = CodexDocument.MigrateFromV1(v1);

        Assert.Equal(CodexDocument.CurrentVersion, doc.Version);
        Assert.Equal(2, doc.Cards.Count);
        Assert.Contains(doc.Types, t => t.Name == "Божество");

        var az = doc.Cards.Single(c => c.Name == "Азатот");
        Assert.True(az.Enabled);
        Assert.Equal("Внешние", az.Group);
        Assert.Contains("Султан демонов", az.Aliases);
        Assert.Equal("deity", az.TypeId);

        Assert.False(doc.Cards.Single(c => c.Name == "Старец").Enabled);
    }

    [Fact]
    public void Codex_json_roundtrip_keeps_cards_links_and_fields()
    {
        var doc = SampleDoc();
        doc.Cards[0].Fields["region"] = FieldValue.FromChoice("Межзвёздное пространство");
        var json = doc.ToJson();
        var back = CodexDocument.FromJson(json);

        Assert.Equal(doc.Cards.Count, back.Cards.Count);
        Assert.Equal(doc.Links.Count, back.Links.Count);
        Assert.Equal("Межзвёздное пространство", back.Cards[0].Fields["region"].Choice);
        Assert.Equal(doc.Types.Count, back.Types.Count);
    }

    // ─────────────────────── B. сервис кодекса ───────────────────────

    [Fact]
    public void Merge_moves_aliases_links_tags_and_removes_duplicate()
    {
        var doc = SampleDoc();
        var primary = doc.Cards.Single(c => c.Name == "Азатот");
        var dup = MakeCard("deity", "Азатот (дубль)", "Azathot", "Слепой бог");
        dup.Tags.Add("внешние");
        dup.Fields["note"] = FieldValue.FromText("дубликат");
        doc.Cards.Add(dup);
        doc.Links.Add(new CardLink(dup.Id, primary.Id, "owns")); // станет петлёй → отбрасывается

        var report = CodexService.MergeCards(doc, primary.Id, dup.Id);

        Assert.DoesNotContain(doc.Cards, c => c.Id == dup.Id);
        Assert.Contains("Азатот", report);
        Assert.Contains("Слепой бог", primary.Aliases);
        Assert.Contains("внешние", primary.Tags);
        Assert.Equal("дубликат", primary.Fields["note"].Text);
        Assert.DoesNotContain(doc.Links, l => l.FromId == dup.Id || l.ToId == dup.Id);
    }

    [Fact]
    public void Find_path_returns_chain_of_labeled_steps()
    {
        var doc = SampleDoc();
        // Наг и Йеб → владеет ← ... ищем путь: Камень → Пнакот (located) уже есть;
        // добавим Наг в Пнакот, чтобы получить цепочку длины 2.
        var nug = doc.Cards.Single(c => c.Name == "Наг и Йеб");
        var pp = doc.Cards.Single(c => c.Name == "Пнакот");
        doc.Links.Add(new CardLink(nug.Id, pp.Id, "located"));

        var stone = doc.Cards.Single(c => c.Name == "Камень Пнакота");
        var path = CodexService.FindPath(doc, stone.Id, nug.Id, maxDepth: 4);

        Assert.NotEmpty(path);
        Assert.Equal(stone.Id, path[0].FromId);
        Assert.Equal(nug.Id, path[^1].ToId);
        Assert.True(path.Length <= 4);
    }

    [Fact]
    public void Find_path_returns_empty_when_unreachable()
    {
        var doc = SampleDoc();
        var lonely = MakeCard("deity", "Одинокий");
        doc.Cards.Add(lonely);
        var az = doc.Cards.Single(c => c.Name == "Азатот");
        Assert.Empty(CodexService.FindPath(doc, lonely.Id, az.Id, maxDepth: 4));
    }

    [Fact]
    public void Suggestions_skip_pairs_already_linked_or_missing_cards()
    {
        var doc = SampleDoc();
        var pairs = new List<(string, string, int)>
        {
            ("Азатот", "Пнакот", 5),      // нет связи → предложение
            ("Наг и Йеб", "Азатот", 40),  // связь уже есть → пропуск
            ("Нет такой", "Пнакот", 3),   // карточки нет → пропуск
        };
        var proposals = CodexService.SuggestLinks(doc, pairs);
        var proposal = Assert.Single(proposals);
        Assert.Equal("Азатот", proposal.FirstName);
        Assert.Equal("Пнакот", proposal.SecondName);
        Assert.Equal(5, proposal.CoCount);
    }

    [Fact]
    public void Filter_matches_query_across_all_fields_and_sorts_by_mentions()
    {
        var doc = SampleDoc();
        doc.Cards.Single(c => c.Name == "Пнакот").Fields["region"] = FieldValue.FromText("Антарктика");

        var byField = CodexService.FilterCards(doc, query: "антарк");
        Assert.Contains(byField, c => c.Name == "Пнакот");

        var byTagOrAlias = CodexService.FilterCards(doc, query: "azathot");
        Assert.Contains(byTagOrAlias, c => c.Name == "Азатот");

        var mentions = new Dictionary<string, int> { ["Азатот"] = 10, ["Пнакот"] = 99 };
        var sorted = CodexService.FilterCards(doc, mentionsByCard: mentions, sort: SortMode.ByMentionsDesc);
        Assert.Equal("Пнакот", sorted[0].Name);
    }

    [Fact]
    public void Type_counts_group_cards_by_category_name()
    {
        var doc = SampleDoc();
        var counts = CodexService.TypeCounts(doc);
        Assert.Equal(2, counts["Божество"]);
        Assert.Equal(1, counts["Город"]);
        Assert.Equal(1, counts["Артефакт"]);
    }

    [Fact]
    public void Slug_produces_safe_file_names()
    {
        Assert.Equal("Наг_и_Йеб", CodexService.Slug("Наг и Йеб?*"));
        Assert.Equal("card", CodexService.Slug("???"));
    }

    // ─────────────────────── C. экспорт/импорт ───────────────────────

    [Fact]
    public void Markdown_notes_write_frontmatter_links_and_index()
    {
        var doc = SampleDoc();
        using var tmp = new TempDir();
        CodexExporters.WriteMarkdownNotes(tmp.Path, doc);

        var index = File.ReadAllText(Path.Combine(tmp.Path, "index.md"));
        Assert.Contains("Азатот", index);
        var cardFile = Directory.GetFiles(Path.Combine(tmp.Path, "cards"), "*.md")
            .First(f => File.ReadAllText(f).Contains("front-matter") || Path.GetFileNameWithoutExtension(f) == "Азатот");
        var text = File.ReadAllText(cardFile);
        Assert.StartsWith("---", text);
        Assert.Contains("type:", text);
        Assert.Contains("[[", text); // викиссылки на связанные карточки
    }

    [Fact]
    public void Csv_import_creates_cards_and_reports_bad_rows()
    {
        var csv = string.Join('\n',
            "type,name,aliases,group,tags,description",
            "Божество,Йог-Сотот,Йог;Sothoth,Внешние,трансформация,\"Тот, кто сквозь время\"",
            "Город,Ультар,,,," ,
            "битая строка без колонок");
        var doc = CodexDocument.CreateEmpty();
        var (created, errors) = CodexExporters.ImportCsv(doc, csv);

        Assert.Equal(2, created);
        Assert.Single(errors);
        Assert.Contains(doc.Cards, c => c.Name == "Йог-Сотот" && c.Aliases.Contains("Sothoth"));
        Assert.Contains(doc.Cards, c => c.Name == "Ультар");
        Assert.Contains(doc.Types, t => t.Name == "Божество"); // категория создана автоматически
    }

    [Fact]
    public void Html_atlas_is_self_contained_with_embedded_data()
    {
        var doc = SampleDoc();
        var html = CodexExporters.BuildHtmlAtlas(doc);
        Assert.StartsWith("<!DOCTYPE html>", html);
        // плейсхолдер обязан быть заменён данными, а данные — содержать карточки
        Assert.DoesNotContain("__CODEX_DATA__", html);
        Assert.Contains("\"cards\":", html);
        Assert.Contains("Азатот", html);
        Assert.Contains("<canvas", html);
    }

    // ─────────────────────── D. поиск по кодексу ───────────────────────

    [Fact]
    public void Codex_definitions_feed_engine_with_aliases_and_enabled_state()
    {
        var doc = SampleDoc();
        doc.Cards.Single(c => c.Name == "Пнакот").Enabled = false;

        var defs = CodexSearchAdapter.ToDefinitions(doc).ToList();
        var d = Assert.Single(defs, x => x.Name == "Азатот");
        Assert.Contains("Azathot", d.Irregular);
        Assert.DoesNotContain(defs, x => x.Name == "Пнакот");

        var typeMap = CodexSearchAdapter.TypeByName(doc);
        Assert.Equal("Божество", typeMap["Азатот"]);
        Assert.Equal("Город", typeMap["Пнакот"]);
    }

    [Fact]
    public void Automaton_finds_card_alias_from_codex_catalog()
    {
        var doc = SampleDoc();
        var defs = CodexSearchAdapter.ToDefinitions(doc).ToList();
        var catalog = new EntityCatalog(defs, generateVariants: false);
        Assert.True(catalog.VariantsByEntity.ContainsKey("Азатот"));
    }
}

/// <summary>Temp directory helper local to this test class.</summary>
internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(), "ea_test_" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

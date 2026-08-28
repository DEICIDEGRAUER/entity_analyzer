using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Exporting;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

// ═══════════════ R1-17: Экспорт в экосистемы исследователя ═══════════════
// Obsidian-Markdown уже есть; здесь добиваем Anki-колоду и Zotero/Excel-CSV.
// Контракт CSV: экспорт симметричен импорту — выгрузка кодекса импортируется
// обратно без потерь карточек.

public class ResearchExportTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ea_resexp_" + Guid.NewGuid().ToString("N"));

    public ResearchExportTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static CodexDocument Document()
    {
        var doc = new CodexDocument();
        var t = new CardType("t_deity", "Божество", "✦", "#A5A5A5");
        doc.Types.Add(t);
        var card = Card.Create(t.Id, "Наг и Йеб");
        card.AddAlias("Йеб");
        card.Group = "Внешние";
        card.Tags.Add("пара");
        card.Description = "Двуликое божество \"науги\"; строка с запятой, и кавычками";
        doc.Cards.Add(card);
        return doc;
    }

    [Fact]
    public void Anki_deck_is_tsv_with_front_and_back()
    {
        var path = Path.Combine(_dir, "deck.txt");
        CodexExporters.WriteAnkiDeck(path, Document());

        var text = File.ReadAllText(path);
        Assert.Contains("#separator:tab", text);   // заголовки импорта Anki
        Assert.Contains("#html:false", text);
        var line = text.Split('\n').First(l => l.StartsWith("Наг и Йеб\t")).TrimEnd('\r');
        var parts = line.Split('\t');
        Assert.Equal(3, parts.Length);             // front, back, tags
        Assert.Contains("Двуликое божество", parts[1]);
        Assert.Equal("пара", parts[2]);
    }

    [Fact]
    public void Anki_deck_escapes_tabs_and_newlines_in_back()
    {
        var doc = Document();
        doc.Cards[0].Description = "строка1\nстрока2\tхвост";
        var path = Path.Combine(_dir, "deck.txt");
        CodexExporters.WriteAnkiDeck(path, doc);

        var text = File.ReadAllText(path);
        var dataLines = text.Split('\n').Where(l => l.Length > 0 && !l.StartsWith('#'));
        Assert.Single(dataLines); // перевод строки внутри поля не рвёт запись
    }

    [Fact]
    public void Research_csv_roundtrip_through_import()
    {
        var csv = CodexExporters.BuildResearchCsv(Document());

        // Симметрия: выгрузка читается штатным импортёром.
        var target = new CodexDocument();
        var (created, errors) = CodexExporters.ImportCsv(target, csv);

        Assert.Empty(errors);
        Assert.Equal(1, created);
        var card = target.Cards.Single();
        Assert.Equal("Наг и Йеб", card.Name);
        Assert.Contains("Йеб", card.Aliases);
        Assert.Equal("Внешние", card.Group);
        Assert.Contains("пара", card.Tags);
        Assert.Contains("запятой", card.Description);
    }

    [Fact]
    public void Research_csv_has_utf8_bom_for_excel()
    {
        var path = Path.Combine(_dir, "zotero.csv");
        CodexExporters.WriteResearchCsv(path, Document());

        var bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length > 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "Excel распознаёт UTF-8 только по BOM");
    }
}

using EntityAnalyzer.Core.Persistence;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

// ═══════════════ R2-20: Печать отчётов — разбор Markdown на блоки ═══════════════
// Нейтральная модель блоков (заголовок/параграф/буллет/таблица) между
// генераторами отчётов (AcademicReport) и рендерами (FlowDocument в App).
// Ядро не знает про WPF; App не парсит текст сам. Подмножество Markdown,
// которым реально пишут отчёты проекта: ATX-заголовки, буллеты, таблицы
// с экранированием канала, параграфы из смежных строк.

public class MarkdownBlocksTests
{
    [Fact]
    public void Empty_input_yields_no_blocks()
    {
        Assert.Empty(MarkdownBlocks.Parse(""));
        Assert.Empty(MarkdownBlocks.Parse("   \n  \n"));
    }

    [Theory]
    [InlineData("# Заголовок", 1, "Заголовок")]
    [InlineData("## Раздел", 2, "Раздел")]
    [InlineData("### Подраздел", 3, "Подраздел")]
    public void Atx_headings_are_parsed_with_level(string line, int level, string text)
    {
        var block = Assert.IsType<MdHeading>(MarkdownBlocks.Parse(line).Single());
        Assert.Equal(level, block.Level);
        Assert.Equal(text, block.Text);
    }

    [Fact]
    public void Consecutive_plain_lines_form_one_paragraph()
    {
        var block = Assert.IsType<MdParagraph>(
            MarkdownBlocks.Parse("Первая строка.\nВторая строка.").Single());
        Assert.Equal("Первая строка. Вторая строка.", block.Text);
    }

    [Fact]
    public void Bullets_are_parsed()
    {
        var blocks = MarkdownBlocks.Parse("- пункт один\n- пункт два");

        Assert.Equal(2, blocks.Count);
        Assert.Equal("пункт один", Assert.IsType<MdBullet>(blocks[0]).Text);
        Assert.Equal("пункт два", Assert.IsType<MdBullet>(blocks[1]).Text);
    }

    [Fact]
    public void Table_is_parsed_with_header_rows_and_unescaped_pipes()
    {
        var md = """
            | Сущность | Упоминаний |
            |---|---:|
            | Ктулху | 12 |
            | A\|B | 3 |
            """;

        var table = Assert.IsType<MdTable>(MarkdownBlocks.Parse(md).Single());

        Assert.Equal(["Сущность", "Упоминаний"], table.Header);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(["Ктулху", "12"], table.Rows[0]);
        // Экранированный канал не рвёт ячейку.
        Assert.Equal(["A|B", "3"], table.Rows[1]);
    }

    [Fact]
    public void Mixed_document_preserves_block_order()
    {
        var md = """
            # Научное резюме

            Вводный абзац,
            перенесённый.

            ## Аннотация

            - факты
            - цифры

            | Ключ | Значение |
            |---|---|
            | A | 1 |
            """;

        var blocks = MarkdownBlocks.Parse(md);

        Assert.Equal(6, blocks.Count);
        Assert.IsType<MdHeading>(blocks[0]);
        Assert.IsType<MdParagraph>(blocks[1]);
        Assert.IsType<MdHeading>(blocks[2]);
        Assert.IsType<MdBullet>(blocks[3]);
        Assert.IsType<MdBullet>(blocks[4]);
        Assert.IsType<MdTable>(blocks[5]);
    }
}

namespace EntityAnalyzer.Core.Persistence;

/// <summary>Базовый тип блока нейтральной модели отчёта (R2-20).</summary>
public abstract record MdBlock;

/// <summary>ATX-заголовок («#», «##», «###»…).</summary>
public sealed record MdHeading(int Level, string Text) : MdBlock;

/// <summary>Абзац: смежные строки, склеенные пробелом.</summary>
public sealed record MdParagraph(string Text) : MdBlock;

/// <summary>Элемент маркированного списка («- текст»).</summary>
public sealed record MdBullet(string Text) : MdBlock;

/// <summary>Таблица: шапка + строки ячеек; экранированный канал восстановлен.</summary>
public sealed record MdTable(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows) : MdBlock;

/// <summary>
/// R2-20: разбор Markdown-отчётов проекта в нейтральную модель блоков.
/// Мост между генераторами (AcademicReport, entities_report.md) и рендерами
/// без WPF-зависимостей в ядре: FlowDocument строится в App из этих блоков.
/// Подмножество языка, которым пишут отчёты: ATX-заголовки, буллеты,
/// таблицы с разделителем «|---|» и экранированием канала «\|», абзацы.
/// Неизвестные конструкции не роняют разбор — строка становится абзацем.
/// </summary>
public static class MarkdownBlocks
{
    private const char EscapedPipePlaceholder = '\u0001';

    public static IReadOnlyList<MdBlock> Parse(string markdown)
    {
        var blocks = new List<MdBlock>();
        if (string.IsNullOrWhiteSpace(markdown))
            return blocks;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        var paragraph = new List<string>();
        var tableLines = new List<string>();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;
            blocks.Add(new MdParagraph(string.Join(" ", paragraph)));
            paragraph.Clear();
        }

        void FlushTable()
        {
            if (tableLines.Count == 0) return;
            var header = SplitRow(tableLines[0]);
            var rows = new List<IReadOnlyList<string>>();
            // Вторая строка таблицы — выравнивающий разделитель «|---|---:|».
            foreach (var line in tableLines.Skip(1))
            {
                if (IsSeparatorRow(line)) continue;
                rows.Add(SplitRow(line));
            }
            if (header.Count > 0)
                blocks.Add(new MdTable(header, rows));
            tableLines.Clear();
        }

        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();

            if (line.StartsWith('|'))
            {
                FlushParagraph();
                tableLines.Add(line);
                continue;
            }
            FlushTable();

            if (line.Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (line.StartsWith('#'))
            {
                FlushParagraph();
                var level = line.TakeWhile(c => c == '#').Count();
                blocks.Add(new MdHeading(level, line[level..].Trim()));
                continue;
            }

            if (line.StartsWith("- "))
            {
                FlushParagraph();
                blocks.Add(new MdBullet(line[2..].Trim()));
                continue;
            }

            paragraph.Add(line.Trim());
        }

        FlushTable();
        FlushParagraph();
        return blocks;
    }

    /// <summary>Разделительная строка таблицы: только каналы, дефисы и двоеточия.</summary>
    private static bool IsSeparatorRow(string line) =>
        line.All(c => c is '|' or '-' or ':' or ' ');

    /// <summary>
    /// Ячейки строки таблицы. Канал внутри ячейки экранирован как «\|»
    /// (см. AcademicReport.Esc) — на время сплита подменяется плейсхолдером.
    /// Крайние пустые элементы от обрамляющих каналов отбрасываются.
    /// </summary>
    private static IReadOnlyList<string> SplitRow(string line)
    {
        var prepared = line.Replace("\\|", EscapedPipePlaceholder.ToString());
        var parts = prepared.Split('|');
        var start = parts.Length > 0 && parts[0].Trim().Length == 0 ? 1 : 0;
        var end = parts.Length - (parts[^1].Trim().Length == 0 ? 1 : 0);
        var cells = new List<string>();
        for (var i = start; i < end; i++)
            cells.Add(parts[i].Trim().Replace(EscapedPipePlaceholder.ToString(), "|"));
        return cells;
    }
}

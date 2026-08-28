using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using EntityAnalyzer.App.Infrastructure;
using EntityAnalyzer.Core.Persistence;

namespace EntityAnalyzer.App.Views;

/// <summary>
/// R2-20: предпросмотр печати отчёта. Нейтральные блоки (MarkdownBlocks)
/// рендерятся в FlowDocument светлой академической темы; печать/PDF — штатным
/// PrintDialog, колонтитулы и номера страниц добавляет HeaderFooterPaginator.
/// </summary>
public partial class PrintPreviewWindow : Window
{
    private readonly string _title;

    public PrintPreviewWindow(string title, IReadOnlyList<MdBlock> blocks)
    {
        InitializeComponent();
        _title = title;
        Title = $"Предпросмотр печати — {title}";
        Viewer.Document = BuildDocument(blocks);
    }

    private FlowDocument BuildDocument(IReadOnlyList<MdBlock> blocks)
    {
        var doc = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Foreground = FindResource("BrushTextPrimary") as Brush ?? Brushes.DimGray,
            Background = Brushes.White,
            PagePadding = new Thickness(48, 40, 48, 56),
        };

        var i = 0;
        while (i < blocks.Count)
        {
            var block = blocks[i];

            switch (block)
            {
                case MdHeading h:
                    doc.Blocks.Add(new Paragraph(new Run(h.Text))
                    {
                        FontSize = h.Level switch { 1 => 20, 2 => 16, _ => 14 },
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, i == 0 ? 0 : 16, 0, 8),
                    });
                    break;

                case MdBullet:
                    // Подряд идущие буллеты собираются в один список.
                    var list = new List { MarkerStyle = TextMarkerStyle.Disc, Margin = new Thickness(0, 0, 0, 8) };
                    while (i < blocks.Count && blocks[i] is MdBullet b)
                    {
                        var item = new ListItem();
                        var itemPara = new Paragraph { Margin = new Thickness(0) };
                        AddInlineRuns(itemPara.Inlines, b.Text);
                        item.Blocks.Add(itemPara);
                        list.ListItems.Add(item);
                        i++;
                    }
                    doc.Blocks.Add(list);
                    continue; // индекс уже съеден внутренним циклом

                case MdTable t:
                    doc.Blocks.Add(BuildTable(t));
                    break;

                case MdParagraph p:
                    var para = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                    AddInlineRuns(para.Inlines, p.Text);
                    doc.Blocks.Add(para);
                    break;
            }

            i++;
        }

        return doc;
    }

    private Table BuildTable(MdTable t)
    {
        var border = FindResource("BrushBorderLight") as Brush ?? Brushes.LightGray;
        var headerBg = FindResource("BrushBgNav") as Brush ?? Brushes.WhiteSmoke;

        var table = new Table { Margin = new Thickness(0, 4, 0, 12) };
        for (var c = 0; c < t.Header.Count; c++)
            table.Columns.Add(new TableColumn());

        // Шапка.
        var headerGroup = new TableRowGroup();
        var headerRow = new TableRow { Background = headerBg, FontWeight = FontWeights.SemiBold };
        foreach (var cell in t.Header)
            headerRow.Cells.Add(MakeCell(cell, border));
        headerGroup.Rows.Add(headerRow);
        table.RowGroups.Add(headerGroup);

        // Данные.
        var bodyGroup = new TableRowGroup();
        foreach (var row in t.Rows)
        {
            var tr = new TableRow();
            foreach (var cell in row)
                tr.Cells.Add(MakeCell(cell, border));
            bodyGroup.Rows.Add(tr);
        }
        table.RowGroups.Add(bodyGroup);

        return table;
    }

    private TableCell MakeCell(string text, Brush border) =>
        new()
        {
            BorderBrush = border,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8, 4, 8, 4),
            Blocks = { MakeParagraph(text) },
        };

    /// <summary>Инлайн-разметка «**жирный**» → SemiBold-прогоны (остальное — обычный текст).</summary>
    private void AddInlineRuns(InlineCollection inlines, string text)
    {
        var parts = text.Split("**");
        for (var i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0) continue;
            inlines.Add(new Run(parts[i])
            {
                FontWeight = i % 2 == 1 ? FontWeights.SemiBold : FontWeights.Normal,
            });
        }
    }

    private Paragraph MakeParagraph(string text)
    {
        var p = new Paragraph { Margin = new Thickness(0) };
        AddInlineRuns(p.Inlines, text);
        return p;
    }

    private void OnPrint(object sender, RoutedEventArgs e)
    {
        var dlg = new System.Windows.Controls.PrintDialog();
        if (dlg.ShowDialog() != true) return;

        // Размер страницы из принтерных настроек — до пагинации.
        var doc = Viewer.Document;
        doc.PageWidth = dlg.PrintableAreaWidth;
        doc.PageHeight = dlg.PrintableAreaHeight;

        var paginator = new HeaderFooterPaginator(
            ((IDocumentPaginatorSource)doc).DocumentPaginator, _title);
        dlg.PrintDocument(paginator, "Entity Analyzer — отчёт");
    }
}

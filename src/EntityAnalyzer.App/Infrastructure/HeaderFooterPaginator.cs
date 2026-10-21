using System.Globalization;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace EntityAnalyzer.App.Infrastructure;

/// <summary>
/// R2-20: обёртка пагинатора для печати — добавляет к каждой странице
/// колонтитулы: название документа сверху слева и «страница N из M» снизу
/// по центру. Сам документ остаётся чистым FlowDocument без визуального мусора
/// в предпросмотре; оформление появляется только на бумаге/PDF.
/// </summary>
public sealed class HeaderFooterPaginator : DocumentPaginator
{
    private readonly DocumentPaginator _source;
    private readonly string _title;

    public HeaderFooterPaginator(DocumentPaginator source, string title)
    {
        _source = source;
        _title = title;
    }

    public override Size PageSize
    {
        get => _source.PageSize;
        set => _source.PageSize = value;
    }

    public override bool IsPageCountValid => _source.IsPageCountValid;
    public override int PageCount => _source.PageCount;
    public override IDocumentPaginatorSource Source => _source.Source;

    public override DocumentPage GetPage(int pageNumber)
    {
        var page = _source.GetPage(pageNumber);

        var chrome = new ContainerVisual();
        chrome.Children.Add(page.Visual);

        // Дип-масштаб для корректного размера текста на высоких DPI.
        var dip = VisualTreeHelper.GetDpi(chrome).PixelsPerDip;
        if (double.IsNaN(dip) || dip <= 0) dip = 1.0;

        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var brush = new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x99)); // нейтральный серый печатного клина

        // Верхний колонтитул: название документа.
        if (!string.IsNullOrWhiteSpace(_title))
        {
            var header = new FormattedText(
                _title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                typeface, 9, brush, dip);
            DrawAt(chrome, header, 48, 20);
        }

        // Нижний колонтитул: номер страницы по центру.
        var footer = new FormattedText(
            $"страница {pageNumber + 1} из {_source.PageCount}",
            CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            typeface, 9, brush, dip);
        DrawAt(chrome, footer, (PageSize.Width - footer.Width) / 2, PageSize.Height - 34);

        return new DocumentPage(chrome, page.Size, page.BleedBox, page.ContentBox);
    }

    private static void DrawAt(ContainerVisual parent, FormattedText text, double x, double y)
    {
        var dv = new DrawingVisual();
        using (var ctx = dv.RenderOpen())
        {
            ctx.DrawText(text, new Point(x, y));
        }
        parent.Children.Add(dv);
    }
}

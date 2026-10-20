using System.Text;
using EntityAnalyzer.Core.Persistence;

namespace EntityAnalyzer.Core.Exporting;

/// <summary>Экспорт цитатника в Markdown (Группа 2, item 26).</summary>
public static class QuoteBookMarkdown
{
    public static string Build(IReadOnlyList<QuoteRecord> quotes, DateTimeOffset exportedAt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Цитатник");
        sb.AppendLine($"> Экспортировано: {exportedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"> Цитат: {quotes.Count}");
        sb.AppendLine();

        var byEntity = quotes.GroupBy(q => q.Entity).OrderBy(g => g.Key, StringComparer.CurrentCulture);
        foreach (var group in byEntity)
        {
            sb.AppendLine($"## {group.Key}");
            sb.AppendLine();
            foreach (var q in group
                         .OrderBy(q => q.FileName, StringComparer.CurrentCulture)
                         .ThenBy(q => q.Line))
            {
                sb.AppendLine(QuoteLines(q.Context));
                sb.AppendLine();
                sb.AppendLine($"*— {q.Entity} ({q.Variant}), стр. {q.Line}, {q.FileName} · отмечено {q.Ts:yyyy-MM-dd}*");
                sb.AppendLine();
            }
        }
        return sb.ToString();
    }

    /// <summary>Многострочный контекст: каждая строка — с префиксом цитаты «&gt; ».</summary>
    private static string QuoteLines(string context)
    {
        var lines = (context ?? "").Trim().Split('\n');
        return string.Join(Environment.NewLine, lines.Select(l => "> " + l.TrimEnd('\r')));
    }
}

/// <summary>Экспорт дневника исследователя в Markdown (Группа 2, item 22/30).</summary>
public static class DiaryMarkdown
{
    public static string Build(IReadOnlyList<StoredNote> notes, DateTimeOffset exportedAt)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Дневник исследователя");
        sb.AppendLine($"> Экспортировано: {exportedAt:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"> Заметок: {notes.Count}");
        sb.AppendLine();

        foreach (var n in notes.OrderBy(n => n.CreatedAt))
        {
            var title = string.IsNullOrWhiteSpace(n.Title)
                ? $"Заметка от {n.CreatedAt:yyyy-MM-dd HH:mm}"
                : n.Title.Trim();
            sb.AppendLine($"## {title}");
            sb.AppendLine();
            sb.AppendLine($"*Создано: {n.CreatedAt:yyyy-MM-dd HH:mm} · обновлено: {n.UpdatedAt:yyyy-MM-dd HH:mm}*");
            if (n.BindingText.Length > 0)
                sb.AppendLine($"*Привязка: {n.BindingText}*");
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(n.Body))
            {
                sb.AppendLine(n.Body.Trim());
                sb.AppendLine();
            }
            sb.AppendLine("---");
            sb.AppendLine();
        }
        return sb.ToString();
    }
}

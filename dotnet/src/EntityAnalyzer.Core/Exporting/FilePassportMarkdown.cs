using System.Text;
using EntityAnalyzer.Core.Analysis;

namespace EntityAnalyzer.Core.Exporting;

/// <summary>
/// Item 24: экспорт паспорта файла в Markdown (текущий срез — WYSIWYG).
/// </summary>
public static class FilePassportMarkdown
{
    public static string Build(FilePassport p)
    {
        var sb = new StringBuilder();
        sb.Append("# Паспорт файла: ").AppendLine(Clean(p.FileName));
        sb.AppendLine();

        if (!p.Found)
        {
            sb.AppendLine(Clean(p.Note));
            return sb.ToString();
        }

        sb.AppendLine($"- Путь: `{Clean(p.FullPath)}`");
        sb.AppendLine($"- Упоминаний: {p.TotalMentions:N0} (автоматически найдено: {p.TotalFound:N0})");
        sb.AppendLine($"- Сущностей: {p.EntityCount:N0}");
        sb.AppendLine($"- Знаков (нормализованный текст): {p.CharCount:N0}");
        sb.AppendLine($"- Плотность: {p.DensityPer10k:0.##} на 10 тыс. знаков");
        if (p.Tags.Count > 0)
            sb.AppendLine($"- Теги: {Clean(string.Join(", ", p.Tags))}");
        sb.AppendLine();

        sb.AppendLine("## Сущности");
        if (p.Entities.Count == 0)
        {
            sb.AppendLine("Сущности не найдены.");
        }
        else
        {
            sb.AppendLine("| Сущность | Упоминаний | Точных | Fuzzy | Варианты |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var e in p.Entities)
                sb.AppendLine($"| {Clean(e.Entity)} | {e.Count} | {e.Exact} | {e.Fuzzy} | {Clean(string.Join(", ", e.Variants))} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Паттерны");
        if (p.Patterns.Count == 0)
        {
            sb.AppendLine("Семантические паттерны не сработали.");
        }
        else
        {
            sb.AppendLine("| Паттерн | Срабатываний | Пример |");
            sb.AppendLine("|---|---|---|");
            foreach (var pat in p.Patterns)
                sb.AppendLine($"| {Clean(pat.PatternName)} | {pat.Count} | {Clean(pat.Example)} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Темы");
        if (p.Themes.Count == 0)
        {
            sb.AppendLine("Темы не определились.");
        }
        else
        {
            sb.AppendLine("| Тема | Упоминаний | Доля |");
            sb.AppendLine("|---|---|---|");
            foreach (var t in p.Themes)
                sb.AppendLine($"| {Clean(t.Theme)} | {t.Count} | {t.Share:0.#}% |");
        }
        sb.AppendLine();

        sb.AppendLine("## Плотность по главам");
        if (p.Chapters.Count == 0)
        {
            sb.AppendLine("Разбивка недоступна.");
        }
        else
        {
            sb.AppendLine("| Глава | Упоминаний | Знаков | Плотность на 10 тыс. |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var c in p.Chapters)
                sb.AppendLine($"| {Clean(c.Label)} | {c.Mentions} | {c.Chars:N0} | {c.DensityPer10k:0.##} |");
        }
        sb.AppendLine();

        sb.AppendLine("## Связи с другими файлами");
        if (p.Neighbors.Count == 0)
        {
            sb.AppendLine("Общих сущностей с другими файлами не найдено.");
        }
        else
        {
            sb.AppendLine("| Файл | Общих сущностей | Примеры |");
            sb.AppendLine("|---|---|---|");
            foreach (var n in p.Neighbors)
                sb.AppendLine($"| {Clean(n.FileName)} | {n.SharedEntities} | {Clean(string.Join(", ", n.Entities))} |");
        }

        if (p.Note.Length > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Примечание");
            sb.AppendLine(Clean(p.Note));
        }
        return sb.ToString();
    }

    private static string Clean(string? s) =>
        (s ?? "").Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
}

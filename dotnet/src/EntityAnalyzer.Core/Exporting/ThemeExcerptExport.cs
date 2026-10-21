using System.Text;
using System.Text.Json;
using EntityAnalyzer.Core.Themes;

namespace EntityAnalyzer.Core.Exporting;

/// <summary>
/// R3-1 (item 52): экспорт отрывков темы в MD/CSV/JSON.
/// Каждая строка несёт файл и позицию (строку) — отрывок можно открыть
/// в исходном тексте.
/// </summary>
public static class ThemeExcerptExport
{
    public static string WriteMarkdown(IReadOnlyList<ThemeExcerpt> excerpts, string theme, string? entity, string path)
    {
        var sb = new StringBuilder();
        sb.Append("# Отрывки: тема «").Append(theme).Append('»');
        if (!string.IsNullOrWhiteSpace(entity)) sb.Append(", сущность «").Append(entity).Append('»');
        sb.AppendLine();
        sb.AppendLine();
        sb.AppendLine($"Отрывков: {excerpts.Count} · файлов: {ThemeExcerptBuilder.FileCount(excerpts)}");
        sb.AppendLine();

        string? current = null;
        foreach (var e in excerpts)
        {
            if (!string.Equals(current, e.FileName, StringComparison.Ordinal))
            {
                current = e.FileName;
                sb.Append("## ").AppendLine(current);
                sb.AppendLine();
            }
            sb.Append("- строка ").Append(e.Line)
              .Append(" · ").Append(e.Entity).Append(" (").Append(e.Variant).Append(')')
              .Append(" · слова темы: ").Append(string.Join(", ", e.Words))
              .AppendLine();
            sb.Append("  > ").AppendLine(Flat(e.Context));
            sb.AppendLine();
        }
        Write(path, sb.ToString());
        return path;
    }

    public static string WriteCsv(IReadOnlyList<ThemeExcerpt> excerpts, string theme, string? entity, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("theme;entity;file;line;variant;words;context");
        foreach (var e in excerpts)
        {
            sb.Append(Escape(theme)).Append(';');
            sb.Append(Escape(e.Entity)).Append(';');
            sb.Append(Escape(e.FileName)).Append(';');
            sb.Append(e.Line).Append(';');
            sb.Append(Escape(e.Variant)).Append(';');
            sb.Append(Escape(string.Join(", ", e.Words))).Append(';');
            sb.AppendLine(Escape(Flat(e.Context)));
        }
        Write(path, sb.ToString());
        return path;
    }

    public static string WriteJson(IReadOnlyList<ThemeExcerpt> excerpts, string theme, string? entity, string path)
    {
        var payload = new
        {
            theme,
            entity = entity ?? "",
            count = excerpts.Count,
            excerpts = excerpts.Select(e => new
            {
                file = e.FileName,
                path = e.FullPath,
                line = e.Line,
                entity = e.Entity,
                variant = e.Variant,
                words = e.Words,
                context = e.Context,
            }).ToList(),
        };
        Write(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        return path;
    }

    private static void Write(string path, string content)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, content, new UTF8Encoding(false));
    }

    private static string Escape(string s) => "\"" + (s ?? "").Replace("\"", "\"\"") + "\"";

    private static string Flat(string? s) =>
        (s ?? "").Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
}

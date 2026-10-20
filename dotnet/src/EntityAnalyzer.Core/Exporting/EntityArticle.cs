using System.Globalization;
using System.Text;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Codex;

namespace EntityAnalyzer.Core.Exporting;

/// <summary>
/// Item 50: полное приведение энциклопедической (вики) статьи о сущности
/// в отчёте. Статья собирается из двух слоёв: карточка кодекса (канон,
/// алиасы, категория, описание, поля, типизированные связи) и результаты
/// прогона (частоты, файлы, темы, цитаты). Пишется папкой «articles»
/// с индексом, а при папочном экспорте — ещё и в папку сущности.
/// </summary>
public static class EntityArticle
{
    public const int MaxQuoteFiles = 40;
    public const int MaxQuotesPerFile = 3;
    public const int MaxThemes = 12;
    private const int MaxLinesPerFile = 8;

    /// <summary>Markdown-статья о сущности; doc = null — только аналитика прогона.</summary>
    public static string Build(string entity, AnalysisSession session, CodexDocument? doc)
    {
        var card = FindCard(doc, entity);
        var mentions = CollectMentions(entity, session, card);

        var sb = new StringBuilder();
        sb.Append("# ").AppendLine(Clean(entity));
        sb.AppendLine();

        AppendCardHeader(sb, entity, card, doc);
        AppendDescription(sb, card);
        AppendFields(sb, card, doc);
        AppendLinks(sb, card, doc);
        AppendPresence(sb, mentions);
        AppendQuotes(sb, mentions);
        return sb.ToString();
    }

    /// <summary>
    /// Пишет статьи по всем сущностям (упоминания прогона ∪ карточки кодекса)
    /// в каталог статей + index.md. Возвращает пути записанных статей.
    /// </summary>
    public static IReadOnlyList<string> WriteAll(AnalysisSession session, CodexDocument? doc, string articlesDir)
    {
        Directory.CreateDirectory(articlesDir);

        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in session.Files.SelectMany(f => f.Mentions))
            if (seen.Add(m.Entity)) names.Add(m.Entity);
        if (doc is not null)
            foreach (var c in doc.Cards)
                if (seen.Add(c.Name)) names.Add(c.Name);
        names.Sort(StringComparer.Ordinal);

        var usedFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "index" };
        var written = new List<string>();
        var index = new StringBuilder();
        index.AppendLine("# Энциклопедические статьи");
        index.AppendLine();
        index.AppendLine($"Сущностей: {names.Count} · файлов корпуса: {session.Files.Count}");
        index.AppendLine();

        foreach (var name in names)
        {
            var file = UniqueFile(usedFiles, name) + ".md";
            var path = Path.Combine(articlesDir, file);
            File.WriteAllText(path, Build(name, session, doc), new UTF8Encoding(false));
            written.Add(path);

            var count = session.Files
                .SelectMany(f => f.Mentions)
                .Count(m => string.Equals(m.Entity, name, StringComparison.OrdinalIgnoreCase));
            index.Append("- [").Append(Clean(name)).Append("](").Append(file)
                 .Append(") — упоминаний: ").AppendLine(count.ToString(CultureInfo.InvariantCulture));
        }

        File.WriteAllText(Path.Combine(articlesDir, "index.md"), index.ToString(), new UTF8Encoding(false));
        return written;
    }

    // ─────────────────────────── слои статьи ───────────────────────────

    private static void AppendCardHeader(StringBuilder sb, string entity, Card? card, CodexDocument? doc)
    {
        if (card is null) return;

        var header = new List<string>();
        if (!string.Equals(card.Canonical, entity, StringComparison.OrdinalIgnoreCase)
            && card.Canonical.Length > 0)
            header.Add($"**Канон:** {Clean(card.Canonical)}");
        if (doc?.FindType(card.TypeId) is { } type)
            header.Add($"**Категория:** {Clean(type.Name)}");
        if (!string.IsNullOrWhiteSpace(card.Group))
            header.Add($"**Группа:** {Clean(card.Group)}");
        if (card.Tags.Count > 0)
            header.Add($"**Теги:** {Clean(string.Join(", ", card.Tags))}");
        if (header.Count > 0)
        {
            sb.AppendLine(string.Join(" · ", header));
            sb.AppendLine();
        }

        var aliases = card.Aliases
            .Where(a => !string.Equals(a, entity, StringComparison.OrdinalIgnoreCase)
                     && !string.Equals(a, card.Canonical, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (aliases.Count > 0)
        {
            sb.Append("*Также: ").Append(Clean(string.Join(", ", aliases))).AppendLine("*");
            sb.AppendLine();
        }
    }

    private static void AppendDescription(StringBuilder sb, Card? card)
    {
        if (card is null || string.IsNullOrWhiteSpace(card.Description)) return;
        sb.AppendLine("## Описание");
        sb.AppendLine();
        sb.AppendLine(card.Description.Trim());
        sb.AppendLine();
    }

    private static void AppendFields(StringBuilder sb, Card? card, CodexDocument? doc)
    {
        if (card is null || card.Fields.Count == 0) return;

        var rows = new List<(string Label, string Value)>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        if (doc?.FindType(card.TypeId) is { } type)
        {
            foreach (var f in type.Fields.OrderBy(f => f.Order))
            {
                if (!card.Fields.TryGetValue(f.Id, out var v)) continue;
                var text = v?.Display() ?? "";
                if (text.Length == 0) continue;
                rows.Add((f.Name, text));
                used.Add(f.Id);
            }
        }
        // Значения без схемы (поле удалено/переименовано) — по идентификатору.
        foreach (var (id, v) in card.Fields)
        {
            if (used.Contains(id)) continue;
            var text = v?.Display() ?? "";
            if (text.Length == 0) continue;
            rows.Add((id, text));
        }
        if (rows.Count == 0) return;

        sb.AppendLine("## Поля");
        sb.AppendLine();
        sb.AppendLine("| Поле | Значение |");
        sb.AppendLine("|---|---|");
        foreach (var (label, value) in rows)
            sb.AppendLine($"| {Clean(label)} | {Clean(value)} |");
        sb.AppendLine();
    }

    private static void AppendLinks(StringBuilder sb, Card? card, CodexDocument? doc)
    {
        if (card is null || doc is null) return;

        var lines = new List<string>();
        foreach (var l in doc.Links.Where(l => l.FromId == card.Id))
        {
            if (doc.FindCard(l.ToId) is not { } target) continue;
            var label = doc.FindRelation(l.RelationId)?.ForwardLabel ?? l.RelationId;
            var note = string.IsNullOrWhiteSpace(l.Note) ? "" : $" — {Clean(l.Note)}";
            lines.Add($"- → {Clean(target.Name)} ({Clean(label)}){note}");
        }
        foreach (var l in doc.Links.Where(l => l.ToId == card.Id))
        {
            if (doc.FindCard(l.FromId) is not { } source) continue;
            var label = doc.FindRelation(l.RelationId)?.ReverseLabel ?? l.RelationId;
            var note = string.IsNullOrWhiteSpace(l.Note) ? "" : $" — {Clean(l.Note)}";
            lines.Add($"- ← {Clean(source.Name)} ({Clean(label)}, обратная){note}");
        }
        if (lines.Count == 0) return;

        sb.AppendLine("## Связи");
        sb.AppendLine();
        foreach (var line in lines) sb.AppendLine(line);
        sb.AppendLine();
    }

    private static void AppendPresence(StringBuilder sb, List<(FileResult File, Mention Mention)> mentions)
    {
        sb.AppendLine("## Присутствие в корпусе");
        sb.AppendLine();
        sb.AppendLine($"- Упоминаний: {mentions.Count.ToString("N0", CultureInfo.InvariantCulture)}");
        if (mentions.Count == 0)
        {
            sb.AppendLine("- В проанализированном корпусе не найдено.");
            sb.AppendLine();
            return;
        }

        var fileGroups = mentions
            .GroupBy(m => m.File.FullPath)
            .Select(g =>
            {
                var lines = g.Select(m => m.Mention.Line)
                    .Where(l => l >= 0)
                    .Distinct()
                    .OrderBy(l => l)
                    .ToList();
                return (Name: g.First().File.FileName, Count: g.Count(), Lines: lines);
            })
            .OrderByDescending(x => x.Count)
            .ToList();

        sb.AppendLine($"- Файлов: {fileGroups.Count.ToString("N0", CultureInfo.InvariantCulture)}");
        sb.AppendLine();

        var themes = mentions
            .Where(m => !string.IsNullOrEmpty(m.Mention.Theme))
            .GroupBy(m => m.Mention.Theme, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count())
            .Take(MaxThemes)
            .ToList();
        if (themes.Count > 0)
        {
            sb.AppendLine("| Тема | Упоминаний |");
            sb.AppendLine("|---|---|");
            foreach (var g in themes)
                sb.AppendLine($"| {Clean(g.Key)} | {g.Count()} |");
            sb.AppendLine();
        }

        sb.AppendLine("| Файл | Упоминаний | Строки |");
        sb.AppendLine("|---|---|---|");
        foreach (var f in fileGroups)
        {
            var lines = f.Lines.Count == 0
                ? "—"
                : string.Join(", ", f.Lines.Take(MaxLinesPerFile))
                  + (f.Lines.Count > MaxLinesPerFile ? ", …" : "");
            sb.AppendLine($"| {Clean(f.Name)} | {f.Count} | {lines} |");
        }
        sb.AppendLine();
    }

    private static void AppendQuotes(StringBuilder sb, List<(FileResult File, Mention Mention)> mentions)
    {
        var byFile = mentions
            .GroupBy(m => m.File.FullPath)
            .OrderByDescending(g => g.Count())
            .Take(MaxQuoteFiles)
            .ToList();
        if (byFile.Count == 0) return;

        sb.AppendLine("## Цитаты");
        sb.AppendLine();
        foreach (var g in byFile)
        {
            sb.Append("### ").AppendLine(Clean(g.First().File.FileName));
            sb.AppendLine();
            foreach (var (_, m) in g.Take(MaxQuotesPerFile))
            {
                var line = m.Line >= 0 ? $" · строка {m.Line}" : "";
                var theme = string.IsNullOrEmpty(m.Theme) ? "" : $" · {Clean(m.Theme)}";
                sb.AppendLine($"> [{m.Num}] {Clean(m.Variant)}{line}{theme}");
                sb.AppendLine($"> {Clean(m.Context)}");
                sb.AppendLine();
            }
        }
    }

    // ─────────────────────────── выборка данных ───────────────────────────

    private static Card? FindCard(CodexDocument? doc, string entity)
    {
        if (doc is null) return null;
        return doc.Cards.FirstOrDefault(c =>
            string.Equals(c.Name, entity, StringComparison.OrdinalIgnoreCase)
            || string.Equals(c.Canonical, entity, StringComparison.OrdinalIgnoreCase)
            || c.Aliases.Contains(entity, StringComparer.OrdinalIgnoreCase));
    }

    private static List<(FileResult File, Mention Mention)> CollectMentions(
        string entity, AnalysisSession session, Card? card)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { entity };
        if (card is not null)
            foreach (var a in card.Aliases) names.Add(a);
        return session.Files
            .SelectMany(f => f.Mentions.Select(m => (File: f, Mention: m)))
            .Where(x => names.Contains(x.Mention.Entity))
            .ToList();
    }

    // ─────────────────────────── служебное ───────────────────────────

    private static string UniqueFile(HashSet<string> used, string entity)
    {
        var baseName = SafeFile(entity);
        var candidate = baseName;
        var n = 2;
        while (!used.Add(candidate)) candidate = baseName + "-" + n++;
        return candidate;
    }

    private static string SafeFile(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name.Trim())
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        var s = sb.ToString().Trim('.', ' ');
        if (s.Length == 0) s = "entity";
        return s.Length > 80 ? s[..80] : s;
    }

    private static string Clean(string? s) =>
        (s ?? "").Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
}

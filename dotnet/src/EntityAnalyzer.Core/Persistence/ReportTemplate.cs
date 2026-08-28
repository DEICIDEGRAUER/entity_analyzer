using System.Text.Json;
using EntityAnalyzer.Core.Analysis;

namespace EntityAnalyzer.Core.Persistence;

/// <summary>Секция шаблона: тип + опциональные колонки/лимит.</summary>
public sealed record TemplateSection(string Type, IReadOnlyList<string>? Columns, int? Top);

/// <summary>
/// R2-29 «Маркетплейс шаблонов отчётов»: декларативный JSON-шаблон выгрузки.
/// Безопасность гарантируется отсутствием исполняемого кода: распознаются
/// только известные типы секций и колонок — всё остальное отвергается
/// с причиной. Сообщество публикует шаблоны как обычные файлы.
/// </summary>
public sealed record ReportTemplate(
    bool IsValid,
    string? Error,
    string Name,
    IReadOnlyList<TemplateSection> Sections)
{
    /// <summary>Известные типы секций (расширение = правка этого множества).</summary>
    public static readonly IReadOnlySet<string> KnownSectionTypes = new HashSet<string>(StringComparer.Ordinal)
    {
        "summary",      // сводка прогона
        "entities",     // таблица сущностей
        "files",        // таблица файлов
        "contexts",     // характерные контексты
    };

    public static readonly IReadOnlySet<string> KnownEntityColumns = new HashSet<string>(StringComparer.Ordinal)
    {
        "entity", "mentions", "files",
    };

    public static readonly IReadOnlySet<string> KnownFileColumns = new HashSet<string>(StringComparer.Ordinal)
    {
        "file", "mentions", "bytes",
    };

    public static ReportTemplate Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return Fail("Корень шаблона должен быть объектом.");

            var name = root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? ""
                : "";
            if (name.Length == 0)
                return Fail("Отсутствует непустое поле «name».");

            if (!root.TryGetProperty("sections", out var sectionsEl) || sectionsEl.ValueKind != JsonValueKind.Array)
                return Fail("Отсутствует массив «sections».");

            var sections = new List<TemplateSection>();
            foreach (var s in sectionsEl.EnumerateArray())
            {
                if (s.ValueKind != JsonValueKind.Object)
                    return Fail("Каждая секция должна быть объектом.");
                var type = s.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() ?? ""
                    : "";
                if (!KnownSectionTypes.Contains(type))
                    return Fail($"Неизвестный тип секции: «{type}». Допустимо: {string.Join(", ", KnownSectionTypes)}.");

                IReadOnlyList<string>? columns = null;
                if (s.TryGetProperty("columns", out var c))
                {
                    if (c.ValueKind != JsonValueKind.Array)
                        return Fail("«columns» должен быть массивом.");
                    columns = [.. c.EnumerateArray().Select(x => x.GetString() ?? "")];
                    var allowed = type == "entities" ? KnownEntityColumns
                        : type == "files" ? KnownFileColumns
                        : null;
                    if (allowed is not null)
                        foreach (var col in columns)
                            if (!allowed.Contains(col))
                                return Fail($"Неизвестная колонка «{col}» для секции «{type}».");
                }

                int? top = null;
                if (s.TryGetProperty("top", out var topEl) && topEl.ValueKind == JsonValueKind.Number)
                    top = topEl.GetInt32();

                sections.Add(new TemplateSection(type, columns, top));
            }

            if (sections.Count == 0)
                return Fail("Шаблон не содержит ни одной секции.");

            return new ReportTemplate(true, null, name, sections);
        }
        catch (JsonException ex)
        {
            return Fail($"Битый JSON: {ex.Message}");
        }
    }

    private static ReportTemplate Fail(string error) => new(false, error, "", []);

    /// <summary>Рендер шаблона в Markdown по данным сессии.</summary>
    public static string Render(ReportTemplate template, AnalysisSession session)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# {template.Name}");
        sb.AppendLine();
        foreach (var section in template.Sections)
        {
            switch (section.Type)
            {
                case "summary":
                    sb.AppendLine("## Сводка");
                    sb.AppendLine();
                    sb.AppendLine($"- Упоминаний: {session.Summary.TotalMentions}");
                    sb.AppendLine($"- Файлов с находками: {session.Summary.FilesWithMentions}/{session.Files.Count}");
                    sb.AppendLine($"- Уникальных сущностей: {session.Summary.UniqueEntities}");
                    break;

                case "entities":
                {
                    sb.AppendLine("## Сущности");
                    sb.AppendLine();
                    var columns = section.Columns ?? ["entity", "mentions"];
                    IEnumerable<KeyValuePair<string, int>> entities = session.Summary.EntityCounts
                        .OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal);
                    if (section.Top is { } top) entities = entities.Take(top);
                    sb.AppendLine("| " + string.Join(" | ", columns.Select(PrettyColumn)) + " |");
                    sb.AppendLine("|" + string.Join("|", columns.Select(_ => "---")) + "|");
                    foreach (var (entity, count) in entities)
                        sb.AppendLine("| " + string.Join(" | ", RenderRow(columns, entity, count, session)) + " |");
                    sb.AppendLine();
                    break;
                }

                case "files":
                {
                    sb.AppendLine("## Файлы");
                    sb.AppendLine();
                    var columns = section.Columns ?? ["file", "mentions"];
                    var files = session.Files.Where(f => f.Error is null && f.Mentions.Count > 0);
                    if (section.Top is { } top) files = files.Take(top);
                    sb.AppendLine("| " + string.Join(" | ", columns.Select(PrettyColumn)) + " |");
                    sb.AppendLine("|" + string.Join("|", columns.Select(_ => "---")) + "|");
                    foreach (var f in files)
                        sb.AppendLine("| " + string.Join(" | ", columns.Select(c => c switch
                        {
                            "file" => f.FileName,
                            "mentions" => f.Mentions.Count.ToString(),
                            "bytes" => f.CharCount.ToString(),
                            _ => "",
                        })) + " |");
                    sb.AppendLine();
                    break;
                }

                case "contexts":
                {
                    sb.AppendLine("## Характерные контексты");
                    sb.AppendLine();
                    var taken = 0;
                    foreach (var f in session.Files.Where(f => f.Error is null))
                    foreach (var m in f.Mentions)
                    {
                        if (section.Top is { } top && taken >= top) break;
                        sb.AppendLine($"- «{m.Context}» ({f.FileName}, строка {m.Line})");
                        taken++;
                    }
                    sb.AppendLine();
                    break;
                }
            }
        }
        return sb.ToString();
    }

    private static string PrettyColumn(string column) => column switch
    {
        "entity" => "Сущность",
        "mentions" => "Упоминаний",
        "files" => "Файлов",
        "file" => "Файл",
        "bytes" => "Байт",
        _ => column,
    };

    private static IEnumerable<string> RenderRow(
        IReadOnlyList<string> columns, string entity, int count, AnalysisSession session)
    {
        foreach (var c in columns)
        {
            yield return c switch
            {
                "entity" => entity,
                "mentions" => count.ToString(),
                "files" => session.Files.Count(f => f.Mentions.Any(m => m.Entity == entity)).ToString(),
                _ => "",
            };
        }
    }
}

/// <summary>Встроенные шаблоны — образцы для сообщества маркетплейса.</summary>
public static class BuiltInTemplates
{
    public const string Chronology = """
        {
          "name": "Хронология событий",
          "sections": [
            { "type": "summary" },
            { "type": "entities", "columns": ["entity", "mentions", "files"], "top": 20 },
            { "type": "contexts", "top": 30 }
          ]
        }
        """;

    public const string FrequencyDictionary = """
        {
          "name": "Частотный словарь",
          "sections": [
            { "type": "entities", "columns": ["entity", "mentions"], "top": 100 }
          ]
        }
        """;

    public const string CharacterMap = """
        {
          "name": "Карта персонажей",
          "sections": [
            { "type": "summary" },
            { "type": "entities", "columns": ["entity", "files"], "top": 50 },
            { "type": "files", "columns": ["file", "mentions"] }
          ]
        }
        """;
}

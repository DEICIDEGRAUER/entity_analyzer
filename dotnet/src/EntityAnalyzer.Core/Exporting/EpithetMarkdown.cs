using System.Text;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Persistence;

namespace EntityAnalyzer.Core.Exporting;

/// <summary>
/// Item 23: экспорт словаря эпитетов в Markdown (текущий срез — WYSIWYG;
/// вызывающий передаёт сущности и вердикты в том виде, как они видны в UI).
/// </summary>
public static class EpithetMarkdown
{
    public static string Build(
        IReadOnlyList<EntityEpithets> data,
        IReadOnlyDictionary<string, string>? verdicts = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Словарь эпитетов");
        sb.AppendLine();

        if (data.Count == 0)
        {
            sb.AppendLine("Эпитеты не собраны — нужен выполненный анализ с упоминаниями.");
            return sb.ToString();
        }

        foreach (var entity in data)
        {
            sb.Append("## ").AppendLine(Clean(entity.Entity));
            sb.AppendLine();
            sb.AppendLine("| Эпитет | Упоминаний | Уникальность | Вердикт |");
            sb.AppendLine("|---|---|---|---|");
            foreach (var e in entity.Epithets)
            {
                var verdict = "";
                if (verdicts is not null &&
                    verdicts.TryGetValue(EpithetStore.MakeKey(entity.Entity, e.Word), out var v))
                {
                    verdict = v switch
                    {
                        EpithetStore.Confirmed => "подтверждён",
                        EpithetStore.Rejected => "отклонён",
                        _ => "",
                    };
                }
                var uniq = e.Unique ? "только у этой сущности" : "также у: " + string.Join(", ", e.AlsoUsedBy);
                sb.AppendLine($"| {Clean(e.Word)} | {e.Count} | {Clean(uniq)} | {verdict} |");
            }
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static string Clean(string? s) =>
        (s ?? "").Replace('|', '/').Replace('\r', ' ').Replace('\n', ' ').Trim();
}

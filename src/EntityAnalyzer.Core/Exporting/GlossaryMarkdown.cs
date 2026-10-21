using System.Text;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Persistence;

namespace EntityAnalyzer.Core.Exporting;

/// <summary>
/// Item 25: экспорт глоссария в Markdown — подтверждённые термины
/// с определениями, затем оставшиеся кандидаты.
/// </summary>
public static class GlossaryMarkdown
{
    public static string Build(
        IReadOnlyList<GlossaryCandidate> candidates,
        IReadOnlyDictionary<string, GlossaryRecord> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Глоссарий терминов");
        sb.AppendLine();

        var confirmed = entries.Values
            .Where(r => r.Status == GlossaryStore.Confirmed)
            .OrderBy(r => r.Word, StringComparer.CurrentCulture)
            .ToList();

        sb.AppendLine("## Подтверждённые термины");
        sb.AppendLine();
        if (confirmed.Count == 0)
        {
            sb.AppendLine("Пока нет подтверждённых терминов.");
        }
        else
        {
            foreach (var rec in confirmed)
            {
                sb.Append("### ").AppendLine(Clean(rec.Word));
                sb.AppendLine();
                sb.AppendLine(rec.Definition.Length > 0 ? Clean(rec.Definition) : "_Определение не записано._");
                sb.AppendLine();
            }
        }

        sb.AppendLine("## Кандидаты");
        sb.AppendLine();
        var pending = candidates
            .Where(c => !entries.TryGetValue(c.Word, out var rec) ||
                        (rec.Status != GlossaryStore.Confirmed && rec.Status != GlossaryStore.Rejected))
            .ToList();
        if (pending.Count == 0)
            sb.AppendLine("Кандидатов нет.");
        else
            foreach (var c in pending)
                sb.AppendLine($"- {Clean(c.Word)} — {c.Count}×");

        return sb.ToString();
    }

    private static string Clean(string? s) => (s ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();
}

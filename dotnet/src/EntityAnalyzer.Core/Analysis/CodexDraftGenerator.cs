using System.Text;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>
/// R2-24 «Авточерновики статей кодекса»: черновик статьи карточки собирается
/// из данных прогона — сводка встречаемости, распределение по файлам и
/// характерные контексты. Офлайн-first: никаких внешних сервисов; человек
/// правит и утверждает черновик вручную.
/// </summary>
public static class CodexDraftGenerator
{
    private const int DefaultMaxContexts = 10;

    public static string Build(AnalysisSession session, string entityName, int maxContexts = DefaultMaxContexts)
    {
        var files = session.Files
            .Where(f => f.Error is null)
            .Select(f => (File: f, Hits: f.Mentions.Where(m => m.Entity == entityName).ToList()))
            .Where(x => x.Hits.Count > 0)
            .OrderByDescending(x => x.Hits.Count)
            .ToList();

        var total = files.Sum(x => x.Hits.Count);
        var sb = new StringBuilder();
        sb.AppendLine($"## {entityName} (черновик)");
        sb.AppendLine();

        if (total == 0)
        {
            sb.AppendLine("*Упоминаний не найдено в проанализированных корпусах.*");
            sb.AppendLine();
            sb.AppendLine("_Статус: черновик требует редактуры и утверждения._");
            return sb.ToString();
        }

        var wordForm = total % 100 is >= 11 and <= 14 ? "упоминаний"
            : total % 10 == 1 ? "упоминание"
            : total % 10 is >= 2 and <= 4 ? "упоминания"
            : "упоминаний";
        sb.AppendLine($"*Авточерновик по результатам анализа: {total} {wordForm} в {files.Count} файлах.*");
        sb.AppendLine();

        sb.AppendLine("### Встречаемость");
        sb.AppendLine();
        foreach (var (file, hits) in files)
            sb.AppendLine($"- {file.FileName} — {hits.Count}");
        sb.AppendLine();

        sb.AppendLine("### Характерные контексты");
        sb.AppendLine();
        var n = 1;
        foreach (var (file, hits) in files)
        foreach (var m in hits.TakeWhile(_ => n <= maxContexts))
        {
            sb.AppendLine($"{n}. «{m.Context}» ({file.FileName}, строка {m.Line})");
            n++;
        }
        sb.AppendLine();

        sb.AppendLine("### Статус");
        sb.AppendLine();
        sb.AppendLine("_Черновик требует редактуры и утверждения. После правки перенесите текст в карточку кодекса._");
        return sb.ToString();
    }
}

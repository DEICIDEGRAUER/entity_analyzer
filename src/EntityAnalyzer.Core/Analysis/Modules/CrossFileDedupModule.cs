using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Filtering;

namespace EntityAnalyzer.Core.Analysis.Modules;

/// <summary>
/// R1-11: межфайловая дедупликация контекстов как модуль завершения прогона.
/// Одинаковые цитаты, скопированные между десятками файлов (справочники),
/// схлопываются в первое вхождение со счётчиком повторов.
/// </summary>
public sealed class CrossFileDedupModule : IAnalysisModule
{
    public string Id => "cross-file-dedup";

    public bool IsEnabled(AnalyzerOptions options) => options.Processing.CrossFileDeduplication;

    public void OnSessionCompleted(SessionModuleContext context)
    {
        if (context.Results.Count == 0) return;
        var (files, grouped) = ContextDeduplicator.Apply(context.Results);
        context.Results = files;
        context.GroupedCount = grouped; // движок передаст в сверку воронки
        if (grouped > 0)
            context.Quality.CountFinalMentions(-grouped);
    }
}

using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Matching;
using EntityAnalyzer.Core.Text;
using EntityAnalyzer.Core.Themes;
using EntityAnalyzer.Core.Variants;

namespace EntityAnalyzer.Core.Analysis.Modules;

/// <summary>
/// R1-11: fuzzy-спасение пропущенных сущностей как модуль обогащения.
/// Выполняется после воронки сущностей и дедупликации контекстов:
/// добавляет упоминания с маркером «[fuzzy:N%]» для имён, не найденных
/// точным проходом. Условия запуска идентичны прежней inline-версии.
/// </summary>
public sealed class FuzzyRescueModule : IAnalysisModule
{
    private Fuzzy.FuzzyFinder? _finder;
    private EntityCatalog? _catalog;

    public string Id => "fuzzy-rescue";

    public bool IsEnabled(AnalyzerOptions options) => options.Processing.FuzzyEnabled;

    public void OnSessionStarting(ModuleSessionContext context)
    {
        _catalog = context.Catalog;
        _finder = new Fuzzy.FuzzyFinder(context.Options.Processing.FuzzyThreshold);
    }

    public void EnrichFile(FileModuleContext context)
    {
        if (_finder is null) return;
        // Спасение имеет смысл только там, где воронка уже нашла что-то:
        // иначе файл почти наверняка не относится к домену корпуса.
        if (context.FoundEntities.Count == 0 || context.Mentions.Count == 0) return;

        using (context.Quality.Measure("fuzzy"))
        {
            foreach (var hit in _finder.FindMissed(
                         _catalog!, context.NormalizedText, context.FoundEntities).Take(20))
            {
                var sentenceIdx = context.NormalizedText.IndexOf(hit.Sentence, StringComparison.Ordinal);
                var line = sentenceIdx >= 0
                    ? AnalysisEngine.CountLineByIndex(context.LineStarts, sentenceIdx)
                    : 0;
                context.Mentions.Add(new Mention(
                    context.Mentions.Count + 1, hit.Entity,
                    $"{hit.Window} [fuzzy:{hit.Score}%]", line,
                    ContextBuilder.CollapseWhitespace(hit.Sentence),
                    Themes.ThemeClassifier.Classify(hit.Sentence),
                    IsFuzzy: true,
                    Offset: sentenceIdx >= 0 ? sentenceIdx : 0)); // R2-13
            }
        }
    }
}

using System.Security.Cryptography;
using System.Text;
using EntityAnalyzer.Core.Configuration;

namespace EntityAnalyzer.Core.Analysis.Modules;

/// <summary>
/// R1-11: семантические паттерны как модуль анализа.
/// Логика перенесена из AnalysisEngine без изменений поведения:
/// загрузка хранилища, матчер, скан нормализованного текста и отпечаток
/// конфигурации «|SEM:…» в подписи кэша (байт-в-байт).
/// </summary>
public sealed class SemanticPatternsModule : IAnalysisModule
{
    private Semantics.SemanticMatcher? _matcher;

    public string Id => "semantic";

    public bool IsEnabled(AnalyzerOptions options) => options.Processing.SemanticPatternsEnabled;

    public void AppendConfigSignature(StringBuilder sb, AnalyzerOptions options)
    {
        if (!options.Processing.SemanticPatternsEnabled) return;
        try
        {
            var store = Semantics.SemanticPatternStore.Load();
            var sem = string.Join("\u0001", store.Patterns
                .Where(x => x.Enabled)
                .OrderBy(x => x.Id, StringComparer.Ordinal)
                .Select(x => $"{x.Id}:{x.Window}:{x.TotalWindow?.ToString() ?? "-"}:" +
                             string.Join(',', x.Tokens.Select(TokenFingerprint))));
            sb.Append("|SEM:").Append(Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(sem)))[..12]);
        }
        catch { /* не удалось прочитать паттерны — сигнатура без SEM-части */ }
    }

    public void OnSessionStarting(ModuleSessionContext context)
    {
        try
        {
            var store = Semantics.SemanticPatternStore.Load();
            _matcher = new Semantics.SemanticMatcher(
                store.Patterns, store.Lexicons,
                context.Catalog.AllNames.ToList(),
                context.Options.Processing.SemanticMaxPerPatternPerFile);
            if (_matcher.HasPatterns)
                Logging.Log.Info("semantic",
                    $"Активных семантических паттернов: {store.Patterns.Count(p => p.Enabled)}");
        }
        catch (Exception ex)
        {
            _matcher = null;
            Logging.Log.Error("semantic", $"Паттерны не загружены: {ex.Message}");
        }
    }

    public void ProcessFile(FileModuleContext context)
    {
        // Паттерны независимы от словаря сущностей: сканируются ВСЕ файлы,
        // включая те, где не нашлось ни одного известного имени.
        if (_matcher is null || !_matcher.HasPatterns) return;
        using (context.Quality.Measure("semantic"))
            context.SemanticMatches.AddRange(
                _matcher.Scan(context.NormalizedText, context.Path, context.LineStarts));
    }

    /// <summary>Отпечаток токена для сигнатуры кэша (включая вложенные опции).</summary>
    private static string TokenFingerprint(Core.Semantics.SemanticToken t) =>
        t.Kind + ":" + t.Value + (t.Required ? "" : "?") +
        (t.Options is { Count: > 0 }
            ? "(" + string.Join("|", t.Options.Select(TokenFingerprint)) + ")"
            : "");
}

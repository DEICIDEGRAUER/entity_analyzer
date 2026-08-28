using System.Text;
using EntityAnalyzer.Core.Configuration;

namespace EntityAnalyzer.Core.Analysis.Modules;

/// <summary>
/// R1-11: упорядоченный набор включённых модулей анализа.
/// Фильтрация по IsEnabled выполняется один раз при создании;
/// подпись кэша агрегируется в порядке подключения модулей.
/// </summary>
public sealed class ModulePipeline
{
    private readonly IReadOnlyList<IAnalysisModule> _modules;
    private readonly AnalyzerOptions _options;

    public ModulePipeline(IEnumerable<IAnalysisModule> modules, AnalyzerOptions options)
    {
        _options = options;
        _modules = modules.Where(m => m.IsEnabled(options)).ToList();
    }

    /// <summary>Идентификаторы включённых модулей (диагностика, логи).</summary>
    public IReadOnlyList<string> EnabledIds => _modules.Select(m => m.Id).ToList();

    public void AppendConfigSignature(StringBuilder sb)
    {
        foreach (var module in _modules)
            module.AppendConfigSignature(sb, _options);
    }

    public void OnSessionStarting(ModuleSessionContext context)
    {
        foreach (var module in _modules)
            module.OnSessionStarting(context);
    }

    public void ProcessFile(FileModuleContext context)
    {
        foreach (var module in _modules)
            module.ProcessFile(context);
    }

    public void EnrichFile(FileModuleContext context)
    {
        foreach (var module in _modules)
            module.EnrichFile(context);
    }

    public void OnSessionCompleted(SessionModuleContext context)
    {
        foreach (var module in _modules)
            module.OnSessionCompleted(context);
    }
}

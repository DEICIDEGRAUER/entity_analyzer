using System.Text;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Variants;

namespace EntityAnalyzer.Core.Analysis.Modules;

/// <summary>
/// R1-11: единый контракт модуля анализа.
///
/// Модуль подключается к движку (<see cref="ModulePipeline"/>) и реализует
/// только нужные ему фазы жизненного цикла прогона (остальные — no-op
/// через default-методы):
///   • <see cref="AppendConfigSignature"/> — вклад в подпись кэша;
///   • <see cref="OnSessionStarting"/> — одноразовые ресурсы прогона;
///   • <see cref="ProcessFile"/> — находки, независимые от словаря сущностей
///     (выполняется до воронки, даже для файлов без известных имён);
///   • <see cref="EnrichFile"/> — обогащение после воронки (fuzzy-спасение);
///   • <see cref="OnSessionCompleted"/> — сводная обработка результатов.
///
/// Воронка сущностей остаётся в движке сознательно: она владеет жизненным
/// циклом кэша и счётчиками воронки; миграция описана в README (#68).
/// </summary>
public interface IAnalysisModule
{
    /// <summary>Стабильный идентификатор (логи, диагностика).</summary>
    string Id { get; }

    /// <summary>Включён ли модуль при данных опциях.</summary>
    bool IsEnabled(AnalyzerOptions options);

    /// <summary>Вклад в подпись конфигурации кэша. Вызывается только у включённых.</summary>
    void AppendConfigSignature(StringBuilder sb, AnalyzerOptions options) { }

    /// <summary>Начало прогона: построить матчеры и другие одноразовые ресурсы.</summary>
    void OnSessionStarting(ModuleSessionContext context) { }

    /// <summary>Фаза 1: скан нормализованного текста до воронки сущностей.</summary>
    void ProcessFile(FileModuleContext context) { }

    /// <summary>Фаза 2: обогащение после построения упоминаний файла.</summary>
    void EnrichFile(FileModuleContext context) { }

    /// <summary>Завершение прогона: сводная обработка всех результатов.</summary>
    void OnSessionCompleted(SessionModuleContext context) { }
}

/// <summary>Одноразовые ресурсы прогона, доступные модулям на старте.</summary>
public sealed class ModuleSessionContext
{
    public required EntityCatalog Catalog { get; init; }
    public required AnalyzerOptions Options { get; init; }
    public required QualityCollector Quality { get; init; }
}

/// <summary>
/// Контекст одного файла для фаз ProcessFile/EnrichFile.
/// Текст уже нормализован; буферы находок принадлежат движку.
/// </summary>
public sealed class FileModuleContext
{
    public required string Path { get; init; }
    public required string NormalizedText { get; init; }
    public required int[] LineStarts { get; init; }
    public required QualityCollector Quality { get; init; }

    /// <summary>Выход фазы 1: семантические и иные независимые находки.</summary>
    public List<SemanticMatch> SemanticMatches { get; } = [];

    /// <summary>Состояние воронки на входе фазы 2 (мутируется обогащающими модулями).</summary>
    public required List<Mention> Mentions { get; init; }
    public required HashSet<string> FoundEntities { get; init; }
}

/// <summary>Контекст завершения прогона: полный упорядоченный список файлов.</summary>
public sealed class SessionModuleContext
{
    /// <summary>Модуль может заменить список (например, межфайловый дедуп).</summary>
    public required List<FileResult> Results { get; set; }
    public required QualityCollector Quality { get; init; }

    /// <summary>Сколько упоминаний снял модуль со сводки — идёт в сверку воронки.</summary>
    public int GroupedCount { get; set; }
}

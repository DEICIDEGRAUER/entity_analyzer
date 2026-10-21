using EntityAnalyzer.Core.Persistence;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>
/// Мост «результат анализа → запись истории». Живёт в слое Analysis, поэтому
/// Persistence не зависит от движка, а модуль истории получает готовую запись
/// одним вызовом и не знает устройства сессии.
/// </summary>
public static class SessionHistoryExtensions
{
    /// <summary>Снимок завершённого прогона для append-only журнала истории.</summary>
    public static RunRecord ToRunRecord(this AnalysisSession session, IReadOnlyList<string> corpusDirs) => new()
    {
        StartedAt = session.StartedAt,
        Preset = session.PresetName,
        TotalMentions = session.Summary.TotalMentions,
        FilesWithMentions = session.Summary.FilesWithMentions,
        UniqueEntities = session.Summary.UniqueEntities,
        DurationSeconds = session.Duration.TotalSeconds,
        RawMatches = session.Quality.RawMatches,
        FinalMentions = session.Quality.FinalMentions,
        CorpusDirs = [.. corpusDirs],
        FilesTotal = session.Files.Count,
        EntityCounts = new Dictionary<string, int>(session.Summary.EntityCounts),
        StageMs = new Dictionary<string, double>(session.Quality.StageMs),
        // QA-UX #13/#16: реальный статус и признак инкрементального прогона.
        Status = session.Files.Count == 0
            ? "Нет файлов"
            : session.Quality.Errors.Count > 0
                ? $"С ошибками ({session.Quality.Errors.Count})"
                : "Успешно",
        FilesFromCache = session.Quality.FilesUnchangedSkipped,
    };
}

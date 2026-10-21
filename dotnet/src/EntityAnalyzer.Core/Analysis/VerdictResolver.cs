using EntityAnalyzer.Core.Persistence;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>
/// Применяет сохранённые вердикты к упоминаниям сессии. Ключ вердикта —
/// (SHA-256 файла, Offset, Variant), поэтому хэш файла передаётся снаружи:
/// анализ уже знает его из инкрементального индекса и не должен перечитывать
/// корпус ради восстановления разметки.
/// </summary>
public static class VerdictResolver
{
    /// <summary>
    /// Применить вердикты к готовой сессии, беря SHA-256 из инкрементального
    /// индекса (без чтения неизменённых файлов) и дочитывая только те файлы,
    /// которых в индексе нет. Удобно для headless/CLI-экспорта: вызывающий
    /// передаёт nullable-индекс, а хэширование-фолбэк остаётся в одном месте.
    /// При пустом журнале корпус не читается, а старые значения очищаются.
    /// </summary>
    public static int ApplyStoredTo(
        AnalysisSession session,
        VerdictStore store,
        IncrementalIndexStore? index)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(store);

        return ApplyTo(session.Files, store, path =>
        {
            if (index is not null && index.TryGetHash(path, out var indexed) &&
                !string.IsNullOrEmpty(indexed))
                return indexed;
            return HashFile(path);
        });
    }

    /// <summary>SHA-256 содержимого файла; null, если файл исчез или недоступен.</summary>
    public static string? HashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(stream));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Проставить вердикты всем файлам с упоминаниями и вернуть число
    /// размеченных цитат. Если журнал пуст, хэши не запрашиваются вовсе.
    /// Для файла с неизвестным хэшем вердикты очищаются: иначе после замены
    /// содержимого на экране остались бы решения от старой версии текста.
    /// </summary>
    public static int ApplyTo(
        IEnumerable<FileResult> files,
        VerdictStore store,
        Func<string, string?> hashForPath)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(hashForPath);

        var records = store.All;
        var index = new Dictionary<(string Hash, long Offset, string Variant), VerdictKind>(
            records.Count);
        foreach (var record in records)
            index[(record.FileHash ?? "", record.Offset, record.Variant ?? "")] = record.Verdict;

        var applied = 0;
        foreach (var file in files)
        {
            if (file.Mentions.Count == 0) continue;

            var hash = index.Count == 0 ? null : hashForPath(file.FullPath);
            foreach (var mention in file.Mentions)
            {
                if (string.IsNullOrEmpty(hash))
                {
                    mention.Verdict = null;
                    continue;
                }

                var offset = mention.Offset > 0 ? mention.Offset : mention.Line;
                mention.Verdict = index.TryGetValue(
                    (hash, offset, mention.Variant ?? ""), out var verdict)
                    ? verdict
                    : null;
                if (mention.Verdict is not null) applied++;
            }
        }

        return applied;
    }
}

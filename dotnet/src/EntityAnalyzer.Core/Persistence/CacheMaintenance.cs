using System.Globalization;

namespace EntityAnalyzer.Core.Persistence;

/// <summary>
/// Обслуживание каталога кэша: сводка для интерфейса (объём, число записей,
/// осиротевшие файлы, диапазон дат) и полная очистка. Читает тот же index.json,
/// что и <see cref="JsonFileCache"/>; любые сбои деградируют в нулевую
/// статистику, а не в исключение — кнопка «Очистить» обязана работать всегда.
/// </summary>
public static class CacheMaintenance
{
    /// <summary>Сводка по каталогу кэша.</summary>
    public sealed record CacheStats(
        string Dir,
        int Entries,
        int FilesCount,
        long FilesBytes,
        int OrphanFiles,
        long OrphanBytes,
        DateTimeOffset? Oldest,
        DateTimeOffset? Newest);

    /// <summary>Прочитать статистику каталога. Несуществующий каталог — пустая сводка.</summary>
    public static CacheStats Inspect(string dir)
    {
        var entries = JsonFileCache.ReadIndex(dir);
        var known = entries.Values.Select(e => e.DataFile).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var files = 0;
        long bytes = 0;
        var orphans = 0;
        long orphanBytes = 0;
        DateTimeOffset? oldest = null, newest = null;

        try
        {
            if (Directory.Exists(dir))
            {
                foreach (var path in Directory.EnumerateFiles(dir))
                {
                    long len;
                    try { len = new FileInfo(path).Length; }
                    catch { continue; }
                    files++;
                    bytes += len;

                    var name = Path.GetFileName(path);
                    if (!name.Equals("index.json", StringComparison.OrdinalIgnoreCase) &&
                        !known.Contains(name))
                    {
                        orphans++;
                        orphanBytes += len;
                    }
                }
            }
        }
        catch { /* перечисление не должно ронять сводку */ }

        foreach (var entry in entries.Values)
        {
            if (!DateTimeOffset.TryParse(entry.Timestamp, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var ts))
                continue;
            if (oldest is null || ts < oldest) oldest = ts;
            if (newest is null || ts > newest) newest = ts;
        }

        return new CacheStats(dir, entries.Count, files, bytes, orphans, orphanBytes, oldest, newest);
    }

    /// <summary>
    /// Полная очистка каталога: удаляет все файлы кэша (записи, индекс,
    /// осиротевшие). Возвращает освобождённые байты. Сам каталог остаётся —
    /// движок продолжит писать в него без пересоздания структуры.
    /// </summary>
    public static long Clear(string dir)
    {
        long freed = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            foreach (var path in Directory.EnumerateFiles(dir))
            {
                try
                {
                    freed += new FileInfo(path).Length;
                    File.Delete(path);
                }
                catch { /* занятый файл — пропускаем, освобождённое считаем по факту */ }
            }
        }
        catch { /* каталог недоступен — считать нечего */ }
        return freed;
    }
}

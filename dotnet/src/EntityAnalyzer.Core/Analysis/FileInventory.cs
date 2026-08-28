using System.Security.Cryptography;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>Строка инвентаризации корпуса: путь + отпечаток содержимого.</summary>
public sealed record FileInventoryEntry(string FullPath, string Sha256, long Bytes);

/// <summary>
/// Инвентаризация корпуса (R2-12): рекурсивный обход каталогов с SHA-256
/// каждого текста. Позволяет рецензенту пакета воспроизводимости доказать,
/// что анализ выполнялся ровно на этих байтах, не пересылая сам корпус.
/// Отсутствующие каталоги молча пропускаются — частичный корпус лучше краха.
/// </summary>
public static class FileInventory
{
    /// <summary>
    /// Рекурсивная инвентаризация. excludeDir — каталог, чьё поддерево
    /// исключается целиком (выходная папка отчётов внутри корпуса: собственные
    /// отчёты не должны попадать в доказательство корпуса пакета .earepro).
    /// </summary>
    public static IReadOnlyList<FileInventoryEntry> Build(
        IEnumerable<string> directories, IEnumerable<string> extensions, string? excludeDir = null)
    {
        var extSet = new HashSet<string>(
            extensions.Select(e => e.StartsWith('.') ? e : "." + e),
            StringComparer.OrdinalIgnoreCase);

        // Нормализуем корень исключения к форме «…\» для надёжного префикс-теста.
        string? excludedRoot = null;
        if (!string.IsNullOrWhiteSpace(excludeDir))
        {
            excludedRoot = Path.GetFullPath(excludeDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
        }

        var result = new List<FileInventoryEntry>();
        foreach (var dir in directories.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                if (excludedRoot is not null &&
                    Path.GetFullPath(file).StartsWith(excludedRoot, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (extSet.Count > 0 && !extSet.Contains(Path.GetExtension(file)))
                    continue;
                try
                {
                    using var stream = File.OpenRead(file);
                    result.Add(new FileInventoryEntry(
                        file,
                        Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(),
                        stream.Length));
                }
                catch (IOException)
                {
                    // Файл занят/исчез между перечислением и чтением — пропускаем.
                }
            }

        // Детерминированный порядок: путь по возрастанию.
        result.Sort((x, y) => string.Compare(x.FullPath, y.FullPath, StringComparison.OrdinalIgnoreCase));
        return result;
    }
}

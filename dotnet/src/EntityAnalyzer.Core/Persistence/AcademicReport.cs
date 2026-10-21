using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace EntityAnalyzer.Core.Persistence;

/// <summary>
/// R1-28: «научное резюме» — академический отчёт поверх готового пакета
/// воспроизводимости .earepro. Документ строится исключительно из содержимого
/// пакета (манифест, запись прогона, хвост журнала, инвентаризация), поэтому
/// рецензенту не нужны ни корпус, ни %APPDATA% автора; цепочка целостности
/// журнала перепроверяется независимо (JournalIntegrity.Verify), а не по
/// заявлению манифеста. Выход — Markdown с инвариантным форматированием чисел:
/// документ воспроизводим на любой локали и готов к конвейеру публикации
/// pandoc → PDF/DOCX/LaTeX. Прямой рендер PDF/DOCX сознательно вне ядра —
/// ноль внешних зависимостей в Core.
/// </summary>
public static class AcademicReport
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Построить academic_summary.md. outputDir null — рядом с пакетом.</summary>
    public static string Write(string packagePath, string? outputDir = null)
    {
        if (!File.Exists(packagePath))
            throw new FileNotFoundException("Пакет воспроизводимости не найден", packagePath);

        var full = ReproPackage.LoadFull(packagePath);
        var targetDir = outputDir ?? Path.GetDirectoryName(Path.GetFullPath(packagePath))!;
        Directory.CreateDirectory(targetDir);
        var outputPath = Path.Combine(targetDir, "academic_summary.md");

        File.WriteAllText(outputPath, Render(full, packagePath), Utf8NoBom);
        return outputPath;
    }

    private static string Render(ReproPackageFull full, string packagePath)
    {
        var run = full.Run;
        var manifest = full.Manifest;
        var md = new StringBuilder();

        // ── Титул ──
        md.AppendLine("# Научное резюме анализа корпуса");
        md.AppendLine();
        md.AppendLine($"**Entity Analyzer** · версия движка {Esc(manifest.EngineVersion)}");
        md.AppendLine();
        if (run is not null)
            md.AppendLine(
                $"Прогон: {FmtDate(run.StartedAt)} · пресет «{Esc(run.Preset)}» · " +
                $"пакет «{Esc(Path.GetFileName(packagePath))}»");
        else
            md.AppendLine(
                $"Пакет «{Esc(Path.GetFileName(packagePath))}» от {FmtDate(manifest.CreatedAt)}");
        md.AppendLine();

        // ── Аннотация ──
        md.AppendLine("## Аннотация");
        md.AppendLine();
        if (run is not null)
        {
            md.AppendLine(
                $"Автоматический анализ корпуса из {N(run.FilesTotal)} файлов ({Megabytes(run.BytesScanned)} МБ) " +
                $"выявил {N(run.TotalMentions)} итоговых цитат, относящихся к {N(run.UniqueEntities)} сущностям; " +
                $"сырых совпадений автомата до конвейера фильтров — {N(run.RawMatches)}. " +
                $"Статус прогона: {Esc(run.Status)}.");
        }
        else
        {
            md.AppendLine("Пакет не содержит записи прогона — доступны только метаданные и инвентаризация.");
        }
        md.AppendLine();

        // ── 1. Методология ──
        md.AppendLine("## 1. Методология");
        md.AppendLine();
        md.AppendLine(
            "Поиск выполнялся автоматом Ахо–Корасика по словарю вариантов имён (морфология, " +
            "транслитерация, порча) с конвейером фильтров, схлопыванием перекрытий вариантов, " +
            "лимитом цитат на файл и межфайловой дедупликацией.");
        md.AppendLine();
        md.AppendLine("| Параметр | Значение |");
        md.AppendLine("|---|---|");
        md.AppendLine($"| Пресет | {Esc(manifest.Preset)} |");
        if (run is not null)
        {
            if (run.CorpusDirs.Count > 0)
                md.AppendLine($"| Каталоги корпуса | {Esc(string.Join("; ", run.CorpusDirs))} |");
            md.AppendLine($"| Файлов в корпусе | {N(run.FilesTotal)} |");
            md.AppendLine($"| Файлов с находками | {N(run.FilesWithMentions)} |");
            md.AppendLine($"| Объём корпуса | {Megabytes(run.BytesScanned)} МБ |");
            md.AppendLine($"| Сырых совпадений автомата | {N(run.RawMatches)} |");
            md.AppendLine($"| Итоговых цитат | {N(run.FinalMentions)} |");
            md.AppendLine($"| Статус прогона | {Esc(run.Status)} |");
            md.AppendLine($"| Длительность, c | {D(run.DurationSeconds)} |");
        }
        md.AppendLine();

        // Тайминги стадий — доказательство измеримости методологии.
        if (run?.StageMs is { Count: > 0 })
        {
            md.AppendLine("### Тайминги стадий");
            md.AppendLine();
            md.AppendLine("| Стадия | мс |");
            md.AppendLine("|---|---:|");
            foreach (var kv in run.StageMs.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
                md.AppendLine($"| {Esc(kv.Key)} | {D(kv.Value)} |");
            md.AppendLine();
        }

        // ── 2. Результаты ──
        md.AppendLine("## 2. Результаты");
        md.AppendLine();
        if (run?.EntityCounts is { Count: > 0 } counts)
        {
            md.AppendLine("Распределение упоминаний по сущностям кодекса:");
            md.AppendLine();
            md.AppendLine("| Сущность | Упоминаний |");
            md.AppendLine("|---|---:|");
            foreach (var kv in counts.OrderByDescending(kv => kv.Value)
                         .ThenBy(kv => kv.Key, StringComparer.Ordinal))
                md.AppendLine($"| {Esc(kv.Key)} | {N(kv.Value)} |");
        }
        else
        {
            md.AppendLine("Результаты недоступны: в пакете нет словаря сущностей.");
        }
        md.AppendLine();

        // ── 3. Воспроизводимость и целостность ──
        md.AppendLine("## 3. Воспроизводимость и целостность");
        md.AppendLine();
        md.AppendLine($"- SHA-256 пакета: {Sha256File(packagePath)}");
        md.AppendLine($"- Записей журнала в пакете: {N(full.Journal.Count)}.");

        // Независимая перепроверка цепочки: рецензент не доверяет манифесту.
        var problems = JournalIntegrity.Verify(full.Journal);
        if (problems.Count == 0)
            md.AppendLine("- Независимая проверка цепочки журнала: целостна.");
        else
        {
            md.AppendLine("- Независимая проверка цепочки журнала: НАРУШЕНА — журнал менялся мимо приложения:");
            foreach (var p in problems.Take(5))
                md.AppendLine($"    - {Esc(p)}");
            if (problems.Count > 5)
                md.AppendLine($"    - … и ещё {N(problems.Count - 5)}");
        }

        if (run is not null && !string.IsNullOrEmpty(run.Hash))
        {
            md.AppendLine($"- Хеш записи прогона: `{run.Hash}`");
            if (!string.IsNullOrEmpty(run.PrevHash))
                md.AppendLine($"- Предыдущее звено (prev_hash): `{run.PrevHash}`");
        }

        if (full.Inventory.Count > 0)
        {
            md.AppendLine();
            md.AppendLine("Снимок инвентаризации корпуса (полные хеши — в inventory.csv внутри пакета):");
            md.AppendLine();
            md.AppendLine("| Показатель | Значение |");
            md.AppendLine("|---|---:|");
            md.AppendLine($"| Файлов в инвентаризации | {N(full.Inventory.Count)} |");
            md.AppendLine($"| Суммарный объём инвентаризации, байт | {N(full.Inventory.Sum(e => e.Bytes))} |");
        }
        else
        {
            md.AppendLine("- Инвентаризация корпуса: пуста.");
        }
        md.AppendLine();

        // ── 4. Ограничения ──
        md.AppendLine("## 4. Ограничения");
        md.AppendLine();
        md.AppendLine("- Пакет не содержит полных текстов корпуса — только пути, объёмы и SHA-256 файлов.");
        md.AppendLine("- Часть совпадений могла быть скрыта лимитом цитат на файл; скрытые упоминания не отклонены, а не показаны.");
        md.AppendLine("- Морфологические варианты и fuzzy-совпадения нормализованы к каноническому имени кодекса.");
        md.AppendLine("- Резюме построено автоматически из пакета воспроизводимости и не заменяет содержательной интерпретации исследователем.");
        md.AppendLine();

        // ── Цитирование ──
        md.AppendLine("## Как цитировать");
        md.AppendLine();
        var preset = run?.Preset ?? manifest.Preset;
        var date = FmtDate(run?.StartedAt ?? manifest.CreatedAt);
        md.AppendLine(
            $"Результаты получены системой Entity Analyzer (версия движка {Esc(manifest.EngineVersion)}), " +
            $"пресет «{Esc(preset)}». Прогон: {date}. " +
            $"Пакет воспроизводимости: {Esc(Path.GetFileName(packagePath))}, " +
            $"SHA-256: {Sha256File(packagePath)}.");

        return md.ToString();
    }

    // Инвариантная культура во всех числах — документ не зависит от локали машины.
    private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);
    private static string D(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Megabytes(long bytes) => (bytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.InvariantCulture);
    private static string FmtDate(DateTimeOffset t) => t.ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture);

    /// <summary>Экранирование ячейки Markdown: канал и переводы строк.</summary>
    private static string Esc(string s) => s
        .Replace("|", "\\|")
        .Replace("\r", " ")
        .Replace("\n", " ");

    private static string Sha256File(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }
}

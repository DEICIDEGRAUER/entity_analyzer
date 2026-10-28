using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Exporting;
using EntityAnalyzer.Core.Variants;

namespace EntityAnalyzer.Core.Persistence;

/// <summary>Writes analysis results as TXT / JSON / CSV / HTML reports and per-entity folders.</summary>
public static partial class ReportExporter
{
    [GeneratedRegex(@"[^\w]+")]
    private static partial Regex SafeName();

    [GeneratedRegex(@"\uFFFD|[\u0000-\u0008]")]
    private static partial Regex ControlChars();

    public static string ExportAll(AnalysisSession session, string outputDir, IReadOnlySet<string> formats,
        bool perEntityFolders = false, EntityCatalog? catalog = null,
        AnalyzerOptions? options = null, CodexDocument? codex = null)
    {
        Directory.CreateDirectory(outputDir);

        if (formats.Contains("txt")) WriteTxt(session, outputDir);
        if (formats.Contains("json")) WriteJson(session, outputDir);
        if (formats.Contains("csv")) WriteCsv(session, outputDir);
        if (formats.Contains("html") || formats.Contains("htm")) WriteHtml(session, outputDir);
        if (formats.Contains("md") || formats.Contains("markdown")) WriteMarkdown(session, outputDir);
        if (perEntityFolders) WritePerEntityFolders(session, Path.Combine(outputDir, "by_entity"), codex);
        if (catalog is not null) WriteVariantsIndex(catalog, Path.Combine(outputDir, "variants"));

        // Item 50: энциклопедические статьи о сущностях — articles/<сущность>.md + index.md.
        if (codex is not null)
            try { EntityArticle.WriteAll(session, codex, Path.Combine(outputDir, "articles")); }
            catch { /* некритично */ }

        // Собственные папки паттернов: by_pattern/<id>/matches.* — как у сущностей.
        try { WritePerPatternFolders(session.SemanticMatches, Path.Combine(outputDir, "by_pattern"), formats); }
        catch { /* некритично */ }

        // Аналитические артефакты (всегда, когда есть данные).
        try { WriteCoOccurrenceGraph(session, outputDir); } catch { /* некритично */ }
        try { WriteVariantCloud(session, outputDir); } catch { /* некритично */ }
        try { WriteTextDuplicates(session, outputDir); } catch { /* некритично */ }
        try { WriteSemanticMatches(session, outputDir); } catch { /* некритично */ }
        if (codex is not null)
            try { WriteTrustCsv(session, outputDir, codex); } catch { /* некритично */ }

        // Максимально подробный сводный отчёт.
        try { WriteFullReport(session, outputDir, options); } catch { /* некритично */ }

        // Протокол проверки: пишется только при наличии вердиктов — набор
        // обычных отчётов остаётся прежним (аддитивно).
        try { WriteReviewReport(session, outputDir); } catch { /* некритично */ }

        return outputDir;
    }

    /// <summary>Отчёт по текстовым дубликатам корпуса: duplicates.txt + duplicates.csv.</summary>
    public static void WriteTextDuplicates(AnalysisSession session, string outputDir)
    {
        if (session.DuplicateGroups.Count == 0) return;
        var sb = new StringBuilder();
        sb.AppendLine("ДУБЛИКАТЫ ТЕКСТОВ КОРПУСА (SHA-256)");
        sb.AppendLine($"Групп: {session.DuplicateGroups.Count}, избыточных файлов: {session.DuplicateGroups.Sum(g => g.RedundantCount)}");
        sb.AppendLine();
        for (var i = 0; i < session.DuplicateGroups.Count; i++)
        {
            var g = session.DuplicateGroups[i];
            sb.AppendLine($"[{i + 1}] {g.Files[0]}");
            foreach (var dup in g.Files.Skip(1))
                sb.AppendLine($"    ≡ {dup}");
            sb.AppendLine($"    hash: {g.Hash[..Math.Min(16, g.Hash.Length)]}…");
            sb.AppendLine();
        }
        File.WriteAllText(Path.Combine(outputDir, "duplicates.txt"), sb.ToString(), new UTF8Encoding(false));

        var csvSb = new StringBuilder();
        csvSb.AppendLine("group,original,duplicate,hash");
        for (var i = 0; i < session.DuplicateGroups.Count; i++)
            foreach (var dup in session.DuplicateGroups[i].Files.Skip(1))
                csvSb.AppendLine($"{i + 1},\"{session.DuplicateGroups[i].Files[0]}\",\"{dup}\",{session.DuplicateGroups[i].Hash}");
        File.WriteAllText(Path.Combine(outputDir, "duplicates.csv"), csvSb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Находки семантических паттернов: semantic_matches.csv + semantic_matches.txt.</summary>
    public static void WriteSemanticMatches(AnalysisSession session, string outputDir)
    {
        if (session.SemanticMatches.Count == 0) return;

        var txt = new StringBuilder();
        txt.AppendLine("СЕМАНТИЧЕСКИЕ ПАТТЕРНЫ — НАХОДКИ");
        txt.AppendLine($"Всего: {session.SemanticMatches.Count}");
        txt.AppendLine();
        foreach (var g in session.SemanticMatches.GroupBy(m => m.PatternName, StringComparer.Ordinal))
        {
            txt.AppendLine($"── {g.Key} ({g.Count()}) ──");
            foreach (var m in g.Take(200))
                txt.AppendLine($"  {Path.GetFileName(m.FilePath)}:{m.Line}  {m.Snippet}");
            txt.AppendLine();
        }
        File.WriteAllText(Path.Combine(outputDir, "semantic_matches.txt"), txt.ToString(), new UTF8Encoding(false));

        var csv = new StringBuilder();
        csv.AppendLine("pattern,file,line,snippet");
        foreach (var m in session.SemanticMatches)
        {
            var name = m.PatternName.Replace(quote, doubleQuote);
            var snippet = m.Snippet.Replace(quote, doubleQuote);
            csv.AppendLine($"\"{name}\",\"{Path.GetFileName(m.FilePath)}\",{m.Line},\"{snippet}\"");
        }
        File.WriteAllText(Path.Combine(outputDir, "semantic_matches.csv"), csv.ToString(), new UTF8Encoding(false));
    }

    /// <summary>
    /// Additive sidecar for the exploratory trusted view. The main
    /// entities_table.csv contract stays eight columns; this file is joined by
    /// file/line/num and never removes rows from the main output.
    /// </summary>
    public static void WriteTrustCsv(AnalysisSession session, string outputDir, CodexDocument codex)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(codex);

        var classifier = TrustViewClassifier.FromCodex(codex);
        var sb = new StringBuilder();
        sb.AppendLine("file,num,entity,variant,line,trust,alias_class,marker,source_quality,source_reason,trust_reason,exploratory");
        foreach (var file in session.Files)
        {
            var fileName = file.FileName;
            var sourceQuality = CsvCell(file.SourceQuality);
            var sourceReason = CsvCell(file.SourceReason);
            var sourceUsable = SourceQuality.IsUsable(file.SourceQuality);
            foreach (var m in file.Mentions)
            {
                var assessment = classifier.Assess(m);
                var trusted = assessment.IsTrusted && sourceUsable;
                var reason = !sourceUsable && assessment.IsTrusted
                    ? string.Equals(file.SourceQuality, SourceQuality.DegradedLexical,
                        StringComparison.Ordinal)
                        ? "lexical trust blocked by degraded source"
                        : $"trust blocked by non-usable source ({file.SourceQuality})"
                    : assessment.Reason;
                var aliasClass = assessment.Level switch
                {
                    TrustViewLevel.Canonical => "canonical",
                    TrustViewLevel.StrongAlias => "strong",
                    TrustViewLevel.Weak => "weak",
                    _ => "unknown",
                };
                sb.Append(CsvCell(fileName)).Append(',')
                    .Append(m.Num).Append(',')
                    .Append(CsvCell(m.Entity)).Append(',')
                    .Append(CsvCell(m.Variant)).Append(',')
                    .Append(m.Line).Append(',')
                    .Append(trusted ? "trusted" : "review").Append(',')
                    .Append(aliasClass).Append(',')
                    .Append(assessment.Marker ? '1' : '0').Append(',')
                    .Append(sourceQuality).Append(',')
                    .Append(sourceReason).Append(',')
                    .Append(CsvCell(reason)).Append(',')
                    .Append("yes")
                    .AppendLine();
            }
        }
        File.WriteAllText(Path.Combine(outputDir, "entities_trust.csv"), sb.ToString(), new UTF8Encoding(false));
    }

    private const string quote = "\"";
    private const string doubleQuote = "\"\"";

    // ---------- протокол проверки (вердикты) ----------

    /// <summary>
    /// Протокол проверки (R2-13): review_report.md + review_report.csv
    /// рядом с обычными отчётами. Пишется только когда в сессии есть хотя бы
    /// один вердикт — без разметки набор файлов экспорта не меняется вовсе,
    /// поэтому обычные отчёты остаются прежними и аддитивность не ломает
    /// существующие конвейеры. В протокол попадают только решённые цитаты;
    /// непроверенные учитываются сводкой («Осталось непроверенных» и файлы).
    /// </summary>
    public static string WriteReviewReport(AnalysisSession session, string outputDir)
    {
        ArgumentNullException.ThrowIfNull(session);

        var summary = ReviewNavigator.Summarize(session.Files);
        if (summary.Reviewed == 0) return "";

        Directory.CreateDirectory(outputDir);

        var decisions = session.Files
            .SelectMany(f => f.Mentions.Select(m => (File: f, Mention: m)))
            .Where(x => x.Mention.Verdict is not null)
            .ToArray();
        var counts = new Dictionary<VerdictKind, int>
        {
            [VerdictKind.Confirmed] = 0,
            [VerdictKind.Rejected] = 0,
            [VerdictKind.Uncertain] = 0,
        };
        foreach (var x in decisions)
            counts[x.Mention.Verdict!.Value] = counts.GetValueOrDefault(x.Mention.Verdict.Value) + 1;

        var mdPath = Path.Combine(outputDir, "review_report.md");
        WriteReviewMarkdown(mdPath, session, summary, counts, decisions);
        WriteReviewCsv(decisions, outputDir);
        return mdPath;
    }

    private static void WriteReviewMarkdown(string path, AnalysisSession session, ReviewSummary summary,
        IReadOnlyDictionary<VerdictKind, int> counts,
        (FileResult File, Mention Mention)[] decisions)
    {
        var sb = new StringBuilder();
        var ci = CultureInfo.InvariantCulture;
        sb.AppendLine("# Протокол проверки упоминаний");
        sb.AppendLine();
        sb.AppendLine(ci, $"- **Пресет:** {session.PresetName}  ");
        sb.AppendLine(ci, $"- **Всего цитат:** {summary.Total:N0}  ");
        sb.AppendLine(ci, $"- **Проверено:** {summary.Reviewed:N0}  ");
        sb.AppendLine(ci, $"- **Подтверждено:** {counts[VerdictKind.Confirmed]:N0}  ");
        sb.AppendLine(ci, $"- **Ложные срабатывания:** {counts[VerdictKind.Rejected]:N0}  ");
        sb.AppendLine(ci, $"- **Спорно:** {counts[VerdictKind.Uncertain]:N0}  ");
        sb.AppendLine(ci, $"- **Осталось непроверенных:** {summary.Unreviewed:N0}  ");
        sb.AppendLine(ci, $"- **Файлов с непроверенными цитатами:** {summary.FilesWithUnreviewed:N0}");
        sb.AppendLine();

        sb.AppendLine("## Решения");
        sb.AppendLine();
        sb.AppendLine("| Файл | Строка | Сущность | Вариант | Вердикт | Смещение |");
        sb.AppendLine("|------|-------:|----------|---------|---------|---------:|");
        foreach (var (file, m) in decisions)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| {MdCell(file.FileName)} | {m.Line} | {MdCell(m.Entity)} | {MdCell(m.Variant)} |" +
                $" {MdCell(m.VerdictLabel)} | {(m.Offset > 0 ? m.Offset : m.Line)} |");
        }
        sb.AppendLine();

        sb.AppendLine("Смещение — позиция находки, по которой ключуется вердикт в verdicts.jsonl");
        sb.AppendLine("(нормализованный текст файла; для старых данных — номер строки).");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Экранирование ячейки Markdown-таблицы: канал, бэкслеш и переносы строк.</summary>
    private static string MdCell(string value) => (value ?? "")
        .Replace("\\", "\\\\")
        .Replace("|", "\\|")
        .Replace("\r", " ")
        .Replace("\n", " ");

    private static void WriteReviewCsv((FileResult File, Mention Mention)[] decisions, string outputDir)
    {
        var path = Path.Combine(outputDir, "review_report.csv");
        var sb = new StringBuilder();
        sb.AppendLine("file;line;offset;entity;variant;verdict");
        foreach (var (file, m) in decisions)
        {
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"{CsvCell(file.FileName)};{m.Line};{(m.Offset > 0 ? m.Offset : m.Line).ToString(CultureInfo.InvariantCulture)};" +
                $"{CsvCell(m.Entity)};{CsvCell(m.Variant)};{CsvCell(VerdictCode(m.Verdict!.Value))}");
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    /// <summary>Стабильный машинный код вердикта для CSV — как в verdicts.jsonl.</summary>
    private static string VerdictCode(VerdictKind kind) => kind switch
    {
        VerdictKind.Confirmed => "confirmed",
        VerdictKind.Rejected => "rejected",
        VerdictKind.Uncertain => "uncertain",
        _ => "",
    };

    private static string CsvCell(string value) =>
        "\"" + (value ?? "").Replace("\"", "\"\"") + "\"";

    /// <summary>Максимально подробный отчёт о прогоне: конфигурация, корпус, воронка,
    /// тайминги стадий, сущности/группы/темы, варианты, повторы, ошибки.</summary>
    public static void WriteFullReport(AnalysisSession session, string outputDir, AnalyzerOptions? options)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, "analysis_report.txt");
        var sb = new StringBuilder();
        var q = session.Quality;
        var s = session.Summary;

        sb.AppendLine("╔════════════════════════════════════════════════════════════╗");
        sb.AppendLine("║          ENTITY ANALYZER — ПОЛНЫЙ ОТЧЁТ О ПРОГОНЕ          ║");
        sb.AppendLine("╚════════════════════════════════════════════════════════════╝");
        sb.AppendLine($"Дата старта      : {session.StartedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Длительность     : {session.Duration.TotalSeconds:0.00} c");
        sb.AppendLine($"Пресет           : {session.PresetName}");
        sb.AppendLine();

        sb.AppendLine("── КОНФИГУРАЦИЯ " + new string('─', 44));
        if (options is not null)
        {
            var c = options.Corpus;
            sb.AppendLine($"Корпус           : {c.Dir}");
            foreach (var d in c.ExtraDirs) sb.AppendLine($"  доп. папка     : {d}");
            sb.AppendLine($"Форматы файлов   : {string.Join(", ", c.Extensions)}   рекурсивно: {(c.Recursive ? "да" : "нет")}   пропуск дубликатов: {(c.SkipDuplicates ? "да" : "нет")}");
            var p = options.Processing;
            sb.AppendLine($"Контекст         : макс {p.ContextMaxWords} / мин {p.ContextMinWords} слов (порог {p.ContextThreshold})");
            sb.AppendLine($"Лимит упоминаний : {p.MaxMentionsPerFile} на файл");
            sb.AppendLine($"Fuzzy-поиск      : {(p.FuzzyEnabled ? $"вкл (порог {p.FuzzyThreshold}%)" : "выкл")}   глубокая порча: {(p.DeepCorrupt ? "вкл" : "выкл")}");
            sb.AppendLine($"Инкрементальный  : {(p.IncrementalEnabled ? "вкл" : "выкл")}");
            sb.AppendLine();
            sb.AppendLine("Фильтры (вкл = участвует в конвейере):");
            sb.AppendLine($"  границы слов         : {(p.BoundaryFiltersEnabled ? "ВКЛ" : "выкл")}");
            sb.AppendLine($"  плотность сущностей  : {(p.ListingDensityEnabled ? "ВКЛ" : "выкл")} (порог сегментов {p.EnumerationMaxSegmentRatio:0.##}, ≤{p.EnumerationMaxSegmentWords} слов)");
            sb.AppendLine($"  чёрный список фраз   : {(p.BlacklistPhrasesEnabled ? $"ВКЛ ({p.ListingBlacklistPhrases.Count} фраз)" : "выкл")}");
            sb.AppendLine($"  осмысленность        : {(p.MeaningfulnessEnabled ? $"ВКЛ (порог {p.MinFunctionWordRatio:0.##})" : "выкл")}");
            sb.AppendLine($"  общие слова          : {(p.CommonWordRuleEnabled ? "ВКЛ" : "выкл")}");
            sb.AppendLine($"  валидация титулов    : {(p.TitleValidationEnabled ? "ВКЛ" : "выкл")}");
            sb.AppendLine($"  межфайловая дедуп    : {(p.CrossFileDeduplication ? "ВКЛ" : "выкл")}");
            sb.AppendLine($"  regex-исключения     : {(p.UserExclusionsEnabled && p.UserExcludePatterns.Count > 0 ? $"ВКЛ ({p.UserExcludePatterns.Count})" : "нет")}");
            foreach (var pat in p.UserExcludePatterns) sb.AppendLine($"    • {pat}");
        }
        sb.AppendLine();

        sb.AppendLine("── КОРПУС " + new string('─', 49));
        sb.AppendLine($"Файлов всего     : {session.Files.Count:N0}");
        sb.AppendLine($"Объём просканиро : {q.BytesScanned / 1024.0 / 1024.0:0.0} МБ");
        sb.AppendLine($"Кодировки        : UTF-8 ≈ {q.Utf8Files}, CP1251 ≈ {q.Cp1251Files}");
        sb.AppendLine($"Символов текста  : {s.TotalChars:N0}");
        sb.AppendLine($"Пропущено без чтения (инкрементально): {q.FilesUnchangedSkipped:N0}");
        // QA N06: сводка тихих пропусков.
        if (q.SkippedTooLarge + q.SkippedEmpty + q.SkippedDuplicate > 0)
            sb.AppendLine($"Пропущено файлов : {q.SkippedTooLarge} больших, {q.SkippedEmpty} пустых, {q.SkippedDuplicate} дубликатов содержимого");
        sb.AppendLine();

        // QA N03: группы текстовых дубликатов — в полный отчёт.
        if (session.DuplicateGroups.Count > 0)
        {
            sb.AppendLine("── ТЕКСТОВЫЕ ДУБЛИКАТЫ " + new string('─', 33));
            var redundant = session.DuplicateGroups.Sum(g => g.RedundantCount);
            sb.AppendLine($"Групп: {session.DuplicateGroups.Count}, избыточных файлов: {redundant}");
            foreach (var g in session.DuplicateGroups.Take(20))
                sb.AppendLine($"  ≡ {Path.GetFileName(g.Files[0])} ≡ {string.Join(", ", g.Files.Skip(1).Select(Path.GetFileName))}");
            if (session.DuplicateGroups.Count > 20)
                sb.AppendLine($"  … и ещё {session.DuplicateGroups.Count - 20} групп (полный список: duplicates.txt)");
            sb.AppendLine();
        }

        sb.AppendLine("── ВОРОНКА ФИЛЬТРОВ " + new string('─', 38));
        sb.AppendLine($"Сырых совпадений автомата   : {q.RawMatches:N0}");
        sb.AppendLine($"  − границы слов             : {q.BoundaryRejected:N0}");
        sb.AppendLine($"  − ломаные слова            : {q.BrokenWordRejected:N0}");
        sb.AppendLine($"  − перечни/оглавления       : {q.ListingRejected:N0}");
        sb.AppendLine($"  − чёрный список фраз       : {q.BlacklistRejected:N0}");
        sb.AppendLine($"  − неосмысленные контексты  : {q.MeaningfulnessRejected:N0}");
        sb.AppendLine($"  − пользовательские regex   : {q.UserPatternRejected:N0}");
        sb.AppendLine($"  − титулы без подтверждения : {q.TitleRejected:N0}");
        sb.AppendLine($"  − общие слова без мифологии: {q.CommonWordRejected:N0}");
        sb.AppendLine($"  − перекрытия вариантов     : {q.OverlapsResolved:N0}");
        sb.AppendLine($"Итоговых цитат               : {q.FinalMentions:N0}");
        sb.AppendLine($"Дубликатов контекстов сгрупп.: {q.DuplicatesGrouped:N0} (+{q.Deduplicated:N0} внутри файлов)");
        sb.AppendLine($"Отклонено фильтрами всего    : {q.TotalFilterRejected:N0} (границы/списки/титулы/шум…)");
        sb.AppendLine($"Скрыто лимитом цитат/файла   : {q.DisplayCapped:N0} (не отклонены — не показаны)");
        sb.AppendLine($"Сверка воронки               : остаток {q.FunnelLeftover:N0} (должно быть 0)");
        sb.AppendLine($"Эффективность границ         : {q.FilterEfficiency}");
        sb.AppendLine($"Кэш                          : попаданий {q.CacheHits:N0}, промахов {q.CacheMisses:N0}");
        if (q.L2Hits + q.L2Misses > 0)
            sb.AppendLine($"Кэш L2 (память)              : попаданий {q.L2Hits:N0}, обращений к диску {q.L2Misses:N0}");
        sb.AppendLine();

        sb.AppendLine("── ТАЙМИНГИ СТАДИЙ " + new string('─', 37));
        foreach (var kv in q.StageMs.OrderByDescending(kv => kv.Value))
            sb.AppendLine($"  {kv.Key.PadRight(12)} : {kv.Value,8:0.0} мс");
        sb.AppendLine();

        if (q.SemanticStats.Count > 0)
        {
            sb.AppendLine("── СЕМАНТИЧЕСКИЕ ПАТТЕРНЫ " + new string('─', 26));
            foreach (var st in q.SemanticStats)
                sb.AppendLine($"  {st.PatternName.PadRight(30)} : {st.Matches,6:N0} (файлов: {st.UniqueFiles})");
            sb.AppendLine();
        }

        sb.AppendLine("── СУЩНОСТИ " + new string('─', 44));
        foreach (var kv in s.EntityCounts.OrderByDescending(kv => kv.Value))
            sb.AppendLine($"  {kv.Key.PadRight(30)} : {kv.Value,6:N0}");
        sb.AppendLine();

        if (s.GroupCounts.Count > 0)
        {
            sb.AppendLine("── ГРУППЫ " + new string('─', 47));
            foreach (var kv in s.GroupCounts.OrderByDescending(kv => kv.Value))
                sb.AppendLine($"  {kv.Key.PadRight(30)} : {kv.Value,6:N0}");
            sb.AppendLine();
        }

        // Темы.
        var themes = session.Files.SelectMany(f => f.Mentions)
            .GroupBy(m => string.IsNullOrEmpty(m.Theme) ? "не определена" : m.Theme)
            .OrderByDescending(g => g.Count());
        sb.AppendLine("── ТЕМАТИКА КОНТЕКСТОВ " + new string('─', 33));
        foreach (var g in themes)
            sb.AppendLine($"  {g.Key.PadRight(30)} : {g.Count(),6:N0}");
        sb.AppendLine();

        // Топ вариантов.
        var variants = session.Files.SelectMany(f => f.Mentions)
            .Where(m => !m.IsFuzzy)
            .GroupBy(m => m.Variant, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).Take(15);
        sb.AppendLine("── ТОП-15 ВАРИАНТОВ НАПИСАНИЯ " + new string('─', 26));
        foreach (var g in variants)
            sb.AppendLine($"  {g.Key.PadRight(30)} : {g.Count(),6:N0}");
        sb.AppendLine();

        // Повторяющиеся контексты.
        var repeated = session.Files.SelectMany(f => f.Mentions).Where(m => m.Repeats > 1)
            .OrderByDescending(m => m.Repeats).Take(20);
        if (repeated.Any())
        {
            sb.AppendLine("── ПОВТОРЯЮЩИЕСЯ КОНТЕКСТЫ (скопированные блоки) " + new string('─', 8));
            foreach (var m in repeated)
                sb.AppendLine($"  ×{m.Repeats}  [{m.Entity}] {m.Context[..Math.Min(90, m.Context.Length)]}…");
            sb.AppendLine();
        }

        if (q.Errors.Count > 0)
        {
            sb.AppendLine("── ОШИБКИ (последние " + q.Errors.Count + ") " + new string('─', 30));
            foreach (var e in q.Errors)
                sb.AppendLine($"  {e.File}: {e.Error}");
            sb.AppendLine();
        }

        sb.AppendLine($"Конец отчёта · Entity Analyzer · {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    // ---------- Feature pack: markdown / graph / cloud / full report ----------

    /// <summary>Markdown-отчёт: сводка, таблица файлов, цитаты по сущностям.</summary>
    public static string WriteMarkdown(AnalysisSession session, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, "entities_report.md");
        var sb = new StringBuilder();

        sb.AppendLine($"# Анализ упоминаний сущностей");
        sb.AppendLine();
        sb.AppendLine($"- **Дата:** {DateTime.Now:yyyy-MM-dd HH:mm}  ");
        sb.AppendLine($"- **Пресет:** {session.PresetName}  ");
        sb.AppendLine($"- **Длительность:** {session.Duration.TotalSeconds:0.0} c  ");
        sb.AppendLine($"- **Упоминаний:** {session.Summary.TotalMentions:N0} в {session.Summary.FilesWithMentions:N0} файлах  ");
        sb.AppendLine($"- **Сущностей:** {session.Summary.UniqueEntities}");
        sb.AppendLine();

        sb.AppendLine("## Сводка по сущностям");
        sb.AppendLine();
        sb.AppendLine("| Сущность | Упоминаний |");
        sb.AppendLine("|----------|-----------:|");
        foreach (var kv in session.Summary.EntityCounts.OrderByDescending(kv => kv.Value))
            sb.AppendLine($"| {kv.Key} | {kv.Value:N0} |");
        sb.AppendLine();

        if (session.Summary.GroupCounts.Count > 0)
        {
            sb.AppendLine("## Группы");
            sb.AppendLine();
            sb.AppendLine("| Группа | Упоминаний |");
            sb.AppendLine("|--------|-----------:|");
            foreach (var kv in session.Summary.GroupCounts.OrderByDescending(kv => kv.Value))
                sb.AppendLine($"| {kv.Key} | {kv.Value:N0} |");
            sb.AppendLine();
        }

        sb.AppendLine("## Файлы");
        sb.AppendLine();
        sb.AppendLine("| Файл | Упоминаний | Плотность/10к |");
        sb.AppendLine("|------|-----------:|--------------:|");
        foreach (var f in session.Files.Where(f => f.Mentions.Count > 0).OrderByDescending(f => f.Mentions.Count))
            sb.AppendLine($"| {f.FileName} | {f.Mentions.Count} | {f.DensityPer10k:0.##} |");
        sb.AppendLine();

        foreach (var group in session.Files.Where(f => f.Mentions.Count > 0)
                     .SelectMany(f => f.Mentions.Select(m => (File: f, M: m)))
                     .GroupBy(x => x.M.Entity))
        {
            sb.AppendLine($"## {group.Key}");
            sb.AppendLine();
            foreach (var (file, m) in group.Take(200))
            {
                var repeat = m.Repeats > 1 ? $" *(×{m.Repeats})*" : "";
                sb.AppendLine($"**{file.FileName}, стр. строка {m.Line}**{repeat} — `{m.Variant}`");
                sb.AppendLine();
                sb.AppendLine($"> {m.Context.Replace("\n", " ")}");
                sb.AppendLine();
            }
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    /// <summary>Интерактивный self-contained HTML-граф ко-упоминаний (canvas + force layout).</summary>
    public static string WriteCoOccurrenceGraph(AnalysisSession session, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var graph = CoOccurrenceGraph.Build(session);
        if (graph.Nodes.Count == 0) return "";

        var path = Path.Combine(outputDir, "cooccurrence_graph.html");
        var json = graph.ToJson().Replace("</", "<\\/");
        var html = """
<!DOCTYPE html>
<html lang="ru"><head><meta charset="utf-8">__CSP__<title>Граф связей сущностей</title>
<style>
body{margin:0;background:#0d0b08;color:#e8dcc0;font-family:'Segoe UI',sans-serif;overflow:hidden}
h1{position:fixed;top:10px;left:16px;font-size:15px;margin:0;color:#c9a227}
#hint{position:fixed;bottom:8px;left:16px;font-size:11px;color:#7a6f57}
canvas{display:block}
</style></head><body>
<h1>Граф связей сущностей</h1><div id="hint">Размер узла ~ упоминания, толщина линии ~ совместные появления</div>
<canvas id="c"></canvas>
<script>
const data = __DATA__;
const cv=document.getElementById('c'),ctx=cv.getContext('2d');
let W,H;function rs(){W=cv.width=innerWidth;H=cv.height=innerHeight;}rs();addEventListener('resize',rs);
const N=data.nodes.map((n,i)=>({...n,x:W/2+Math.cos(i*2.4)*200,y:H/2+Math.sin(i*2.4)*200,vx:0,vy:0}));
const maxW=Math.max(...data.links.map(l=>l.w),1);
function step(){
 for(let i=0;i<N.length;i++)for(let j=i+1;j<N.length;j++){
  const a=N[i],b=N[j],dx=b.x-a.x,dy=b.y-a.y,d2=dx*dx+dy*dy+1,f=2600/d2;
  const d=Math.sqrt(d2);a.vx-=dx/d*f;a.vy-=dy/d*f;b.vx+=dx/d*f;b.vy+=dy/d*f;}
 for(const l of data.links){
  const a=N.find(n=>n.id===l.s),b=N.find(n=>n.id===l.t);if(!a||!b)continue;
  const dx=b.x-a.x,dy=b.y-a.y,d=Math.sqrt(dx*dx+dy*dy)||1;
  const target=90+60*(1-l.w/maxW),f=(d-target)*0.004;
  a.vx+=dx/d*d*f;a.vy+=dy/d*d*f;b.vx-=dx/d*d*f;b.vy-=dy/d*d*f;}
 for(const n of N){n.vx*=0.85;n.vy*=0.85;n.x+=n.vx;n.y+=n.vy;
  n.x=Math.max(40,Math.min(W-40,n.x));n.y=Math.max(40,Math.min(H-40,n.y));}
}
function draw(){
 ctx.fillStyle='#0d0b08';ctx.fillRect(0,0,W,H);
 const maxN=Math.max(...N.map(n=>n.w),1);
 for(const l of data.links){
  const a=N.find(n=>n.id===l.s),b=N.find(n=>n.id===l.t);if(!a||!b)continue;
  ctx.strokeStyle='rgba(201,162,39,'+(0.15+0.5*l.w/maxW)+')';
  ctx.lineWidth=1+3*l.w/maxW;ctx.beginPath();ctx.moveTo(a.x,a.y);ctx.lineTo(b.x,b.y);ctx.stroke();}
 for(const n of N){
  const r=6+18*n.w/maxN;
  ctx.fillStyle='#c9a227';ctx.beginPath();ctx.arc(n.x,n.y,r,0,7);ctx.fill();
  ctx.fillStyle='#e8dcc0';ctx.font=(11+r/3)+'px Segoe UI';
  ctx.fillText(n.id,n.x+r+4,n.y+4);}
}
(function loop(){step();draw();requestAnimationFrame(loop);})();
// drag
let drag=null;
cv.onmousedown=e=>{drag=N.find(n=>{const dx=n.x-e.clientX,dy=n.y-e.clientY;return dx*dx+dy*dy<400});};
cv.onmousemove=e=>{if(drag){drag.x=e.clientX;drag.y=e.clientY;}};
cv.onmouseup=()=>drag=null;
</script></body></html>
""".Replace("__CSP__", CspMeta(CspScripted)).Replace("__DATA__", json);
        File.WriteAllText(path, html, new UTF8Encoding(false));
        return path;
    }

    /// <summary>Облако вариантов имени: частоты форм написания в self-contained HTML.</summary>
    public static string WriteVariantCloud(AnalysisSession session, string outputDir)
    {
        Directory.CreateDirectory(outputDir);

        // Частоты вариантов (без fuzzy-меток).
        var freq = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in session.Files.SelectMany(f => f.Mentions))
        {
            if (m.IsFuzzy) continue;
            var v = m.Variant.Trim();
            if (v.Length == 0) continue;
            freq[v] = freq.GetValueOrDefault(v) + 1;
        }

        // Текстовая статистика рядом.
        var txtPath = Path.Combine(outputDir, "variants_frequency.txt");
        var lines = freq.OrderByDescending(kv => kv.Value)
            .Select(kv => $"{kv.Value}\t{kv.Key}");
        File.WriteAllLines(txtPath, ["частота\tвариант", .. lines], new UTF8Encoding(false));

        if (freq.Count == 0) return "";

        var path = Path.Combine(outputDir, "variant_cloud.html");
        var items = freq.OrderByDescending(kv => kv.Value).Take(120)
            .Select(kv => new { t = kv.Key, w = kv.Value });
        var json = JsonSerializer.Serialize(items,
            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

        var html = """
<!DOCTYPE html>
<html lang="ru"><head><meta charset="utf-8">__CSP__<title>Облако вариантов имени</title>
<style>
body{margin:0;background:#0d0b08;color:#e8dcc0;font-family:'Segoe UI',sans-serif}
h1{font-size:15px;margin:12px 16px;color:#c9a227}
#cloud{text-align:center;line-height:1.9;padding:20px}
span{display:inline-block;margin:0 12px;cursor:default}
</style></head><body>
<h1>Облако вариантов имени (по частоте в корпусе)</h1><div id="cloud"></div>
<script>
const items=__DATA__;
const max=Math.max(...items.map(i=>i.w)),min=Math.min(...items.map(i=>i.w));
const el=document.getElementById('cloud');
for(const it of items){
 const s=document.createElement('span');
 const k=max===min?0:(it.w-min)/(max-min);
 s.textContent=it.t;
 s.style.fontSize=(13+k*34).toFixed(1)+'px';
 s.style.color=`hsl(${42-k*14},${55+k*25}%,${72-k*30}%)`;
 s.title='встречается '+it.w+' раз';
 el.appendChild(s);
 el.appendChild(document.createTextNode(' '));
}
</script></body></html>
""".Replace("__CSP__", CspMeta(CspScripted)).Replace("__DATA__", json);
        File.WriteAllText(path, html, new UTF8Encoding(false));
        return path;
    }

    // ---------- TXT ----------

    public static string WriteTxt(AnalysisSession session, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, "entities_report.txt");
        var sb = new StringBuilder();

        sb.AppendLine("ENTITIES MENTIONS ANALYSIS");
        sb.AppendLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm}   Preset: {session.PresetName}");
        sb.AppendLine($"Total: {session.Summary.TotalMentions} | Files: {session.Summary.FilesWithMentions} | Entities: {session.Summary.UniqueEntities}");
        var q = session.Quality;
        sb.AppendLine($"QUALITY: efficiency={q.FilterEfficiency} raw={q.RawMatches} final={q.FinalMentions} cache={q.CacheHits}h/{q.CacheMisses}m errors={q.Errors.Count} time={q.Duration}");
        foreach (var st in q.SemanticStats)
            sb.AppendLine($"SEMANTIC {st.PatternId}: {st.Matches} matches ({st.UniqueFiles} unique files)");
        sb.AppendLine();
        sb.AppendLine("ENTITY COUNTS:");
        foreach (var (entity, count) in session.Summary.EntityCounts.OrderByDescending(kv => kv.Value))
            sb.AppendLine(CultureInfo.InvariantCulture, $"  {entity}: {count}");

        sb.AppendLine();
        sb.AppendLine("DENSITY TOP 10:");
        foreach (var (i, f) in session.Files
                     .Where(f => f.CharCount > 0)
                     .OrderByDescending(f => f.DensityPer10k)
                     .Take(10).Select((f, idx) => (idx + 1, f)))
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"  {i}. {f.FileName}: {f.DensityPer10k:0.##}/10k ({f.TotalFound}m/{f.CharCount}c)");

        sb.AppendLine();
        sb.AppendLine(new string('=', 64));
        sb.AppendLine();

        foreach (var file in session.Files.Where(f => f.Mentions.Count > 0))
        {
            sb.AppendLine($"FILE: {file.FileName}");
            sb.AppendLine(CultureInfo.InvariantCulture,
                $"Mentions: {file.Mentions.Count}/{file.TotalFound} | d:{file.DensityPer10k:0.##}/10k");
            sb.AppendLine();
            foreach (var m in file.Mentions)
            {
                var theme = m.Theme.Length > 0 ? $" [{m.Theme}]" : "";
                var fuzzyTag = m.IsFuzzy ? " (fuzzy)" : "";
                sb.AppendLine(CultureInfo.InvariantCulture, $"[{m.Num}] {m.Entity} ({m.Variant}) L{m.Line}{theme}{fuzzyTag}");
                sb.AppendLine(m.Context);
                sb.AppendLine();
            }
            sb.AppendLine("---");
            sb.AppendLine();
        }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    // ---------- JSON ----------

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string WriteJson(AnalysisSession session, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, "entities_data.json");

        var payload = new
        {
            generated_at = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
            preset = session.PresetName,
            stats = new
            {
                total = session.Summary.TotalMentions,
                files = session.Summary.FilesWithMentions,
                entities = session.Summary.UniqueEntities,
                entities_dist = session.Summary.EntityCounts,
                density_top10 = session.Files
                    .Where(f => f.CharCount > 0)
                    .OrderByDescending(f => f.DensityPer10k)
                    .Take(10)
                    .Select(f => new { name = f.FileName, density = f.DensityPer10k, mentions = f.TotalFound })
            },
            files = session.Files.Select(f => new
            {
                name = f.FileName,
                path = f.FullPath,
                shown = f.Mentions.Count,
                total = f.TotalFound,
                density = f.DensityPer10k,
                mentions = f.Mentions
            }),
            quality = new
            {
                efficiency = session.Quality.FilterEfficiency,
                raw_matches = session.Quality.RawMatches,
                final_mentions = session.Quality.FinalMentions
            }
        };

        File.WriteAllText(path, JsonSerializer.Serialize(payload, JsonOpts), new UTF8Encoding(false));
        return path;
    }

    // ---------- CSV ----------

    public static string WriteCsv(AnalysisSession session, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, "entities_table.csv");
        var sb = new StringBuilder();
        sb.AppendLine("file;num;entity;variant;line;density_per_10k;theme;context");

        foreach (var file in session.Files)
            foreach (var m in file.Mentions)
            {
                sb.Append(Escape(file.FileName)).Append(';');
                sb.Append(m.Num).Append(';');
                sb.Append(Escape(m.Entity)).Append(';');
                sb.Append(Escape(m.Variant)).Append(';');
                sb.Append(m.Line).Append(';');
                sb.Append(file.DensityPer10k.ToString(CultureInfo.InvariantCulture)).Append(';');
                sb.Append(Escape(m.Theme)).Append(';');
                sb.AppendLine(Escape(ControlChars().Replace(m.Context, " ")));
            }

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;

        static string Escape(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
    }

    // ---------- HTML ----------

    // R1-20: корпус — недоверенный ввод (stored-XSS через имена файлов, варианты,
    // контексты). Каждый self-contained отчёт объявляет CSP: без скриптов — полностью
    // закрыты; интерактивные (canvas-граф, облако, атлас) — только inline-скрипт,
    // любые внешние загрузки запрещены. Экранирование см. Escape ниже.
    internal const string CspStatic = "default-src 'none'; style-src 'unsafe-inline'";
    internal const string CspScripted = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'";

    internal static string CspMeta(string policy) =>
        $"<meta http-equiv=\"Content-Security-Policy\" content=\"{policy}\">";

    public static string WriteHtml(AnalysisSession session, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        var path = Path.Combine(outputDir, "entities_report.html");
        var sb = new StringBuilder();

        sb.Append("""<!DOCTYPE html><html lang="ru"><head><meta charset="utf-8">""");
        sb.Append(CspMeta(CspStatic)); // R1-20
        sb.Append("<title>Entity Analyzer — отчёт</title><style>");
        sb.Append("""
            :root{color-scheme:dark}
            body{background:#14161C;color:#E8E6DF;font:14px/1.55 'Segoe UI',system-ui,sans-serif;margin:0;padding:32px}
            h1{font-size:20px;color:#D4AF37;letter-spacing:.06em;text-transform:uppercase}
            .stats{display:flex;gap:12px;margin:16px 0 24px;flex-wrap:wrap}
            .tile{background:#1B1E27;border:1px solid #333846;border-radius:8px;padding:10px 16px}
            .tile b{display:block;font-size:22px;color:#D4AF37}
            mark{background:#6B5A1E;color:#FFE9A0;border-radius:3px;padding:0 2px}
            .file{margin-bottom:28px}
            .file h2{font-size:15px;color:#9AA0AC;font-weight:600}
            .mention{background:#22262F;border:1px solid #333846;border-radius:8px;padding:10px 14px;margin:8px 0;white-space:pre-wrap}
            .meta{color:#9AA0AC;font-size:12px;margin-bottom:4px}
            """);
        sb.Append("</style></head><body>");
        sb.Append("<h1>Entity Analyzer</h1>");

        var s = session.Summary;
        sb.Append($"""
            <div class="stats">
              <div class="tile"><b>{s.TotalMentions}</b>упоминаний</div>
              <div class="tile"><b>{s.FilesWithMentions}</b>файлов с находками</div>
              <div class="tile"><b>{s.UniqueEntities}</b>сущностей</div>
              <div class="tile"><b>{Escape(session.PresetName)}</b>профиль</div>
            </div>
            """);

        foreach (var file in session.Files.Where(f => f.Mentions.Count > 0))
        {
            sb.Append(CultureInfo.InvariantCulture,
                $"<div class='file'><h2>{Escape(file.FileName)} — {file.Mentions.Count}/{file.TotalFound}, плотность {file.DensityPer10k:0.##}/10k</h2>");
            foreach (var m in file.Mentions)
            {
                sb.Append("<div class='mention'><div class='meta'>")
                  .Append(CultureInfo.InvariantCulture,
                      $"[{m.Num}] {Escape(m.Entity)} · {Escape(m.Variant)} · строка {m.Line}{(m.Theme.Length > 0 ? " · " + Escape(m.Theme) : "")}")
                  .Append("</div>")
                  .Append(Highlight(Escape(m.Context), m.Variant))
                  .Append("</div>");
            }
            sb.Append("</div>");
        }

        sb.Append("</body></html>");
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    internal static string Highlight(string escapedContext, string variant)
    {
        if (string.IsNullOrEmpty(variant)) return escapedContext;
        var idx = escapedContext.IndexOf(variant, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return escapedContext;
        return escapedContext[..idx]
               + "<mark>" + escapedContext.Substring(idx, variant.Length) + "</mark>"
               + escapedContext[(idx + variant.Length)..];
    }

    /// <summary>
    /// Собственные папки паттернов: by_pattern/&lt;pattern-id&gt;/matches.txt|csv|json.
    /// Аналог per-entity папок: каждый паттерн — своя подпапка со своими находками.
    /// </summary>
    public static void WritePerPatternFolders(IReadOnlyList<SemanticMatch> matches,
        string rootDir, IReadOnlySet<string>? formats = null)
    {
        if (matches.Count == 0) return;
        var wantTxt = formats is null || formats.Count == 0 || formats.Contains("txt");
        var wantCsv = formats is null || formats.Contains("csv");
        var wantJson = formats is null || formats.Contains("json");

        foreach (var g in matches.GroupBy(m => m.PatternId, StringComparer.Ordinal))
        {
            var safeId = SanitizeDirName(g.Key);
            var dir = Path.Combine(rootDir, safeId);
            Directory.CreateDirectory(dir);

            if (wantTxt)
            {
                var txt = new StringBuilder();
                txt.AppendLine($"ПАТТЕРН: {g.First().PatternName} ({g.Key})");
                txt.AppendLine($"Находок: {g.Count()}");
                txt.AppendLine();
                foreach (var m in g)
                    txt.AppendLine($"{Path.GetFileName(m.FilePath)}:{m.Line}  {m.Snippet}");
                File.WriteAllText(Path.Combine(dir, "matches.txt"), txt.ToString(), new UTF8Encoding(false));
            }

            if (wantCsv)
            {
                var csv = new StringBuilder();
                csv.AppendLine("file,line,snippet");
                foreach (var m in g)
                    csv.AppendLine($"\"{Path.GetFileName(m.FilePath).Replace(quote, doubleQuote)}\",{m.Line},\"{m.Snippet.Replace(quote, doubleQuote)}\"");
                File.WriteAllText(Path.Combine(dir, "matches.csv"), csv.ToString(), new UTF8Encoding(false));
            }

            if (wantJson)
            {
                var json = System.Text.Json.JsonSerializer.Serialize(
                    new
                    {
                        pattern = g.Key,
                        name = g.First().PatternName,
                        count = g.Count(),
                        matches = g.Select(m => new
                        {
                            file = Path.GetFileName(m.FilePath),
                            line = m.Line,
                            snippet = m.Snippet,
                        }).ToArray(),
                    },
                    new System.Text.Json.JsonSerializerOptions
                    {
                        WriteIndented = true,
                        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                    });
                File.WriteAllText(Path.Combine(dir, "matches.json"), json, new UTF8Encoding(false));
            }
        }
    }

    private static string SanitizeDirName(string id)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(id.Length);
        foreach (var c in id)
            sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
        return sb.Length == 0 ? "unnamed" : sb.ToString();
    }

    // ---------- per-entity folders ----------

    public static string WritePerEntityFolders(AnalysisSession session, string baseDir, CodexDocument? codex = null)
    {
        Directory.CreateDirectory(baseDir);

        foreach (var group in session.Files
                     .SelectMany(f => f.Mentions.Select(m => (File: f, Mention: m)))
                     .GroupBy(x => x.Mention.Entity, StringComparer.Ordinal))
        {
            var safe = SafeName().Replace(group.Key, "_");
            var entityDir = Path.Combine(baseDir, safe);
            Directory.CreateDirectory(entityDir);

            // All mentions of one entity.
            var sb = new StringBuilder();
            sb.AppendLine(CultureInfo.InvariantCulture, $"ENTITY: {group.Key}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Total: {group.Count()}");
            sb.AppendLine(CultureInfo.InvariantCulture, $"Files: {group.Select(x => x.File.FileName).Distinct().Count()}");
            sb.AppendLine();
            string? currentFile = null;
            foreach (var (file, mention) in group)
            {
                if (mention.Line >= 0 && !Equals(currentFile, file.FileName))
                {
                    currentFile = file.FileName;
                    sb.AppendLine(CultureInfo.InvariantCulture, $"\n--- {currentFile} ---\n");
                }
                sb.AppendLine(CultureInfo.InvariantCulture, $"[{mention.Num}] {mention.Variant} L{mention.Line}");
                sb.AppendLine(mention.Context);
                sb.AppendLine();
            }
            File.WriteAllText(Path.Combine(entityDir, safe + "_mentions.txt"), sb.ToString(), new UTF8Encoding(false));

            // Item 50: энциклопедическая статья о сущности — рядом с упоминаниями.
            if (codex is not null)
                File.WriteAllText(Path.Combine(entityDir, safe + "_article.md"),
                    EntityArticle.Build(group.Key, session, codex), new UTF8Encoding(false));

            // Theme subfolders.
            foreach (var themeGroup in group.GroupBy(x => x.Mention.Theme, StringComparer.Ordinal))
            {
                if (string.IsNullOrEmpty(themeGroup.Key)) continue;
                var themeSafe = themeGroup.Key.Replace(' ', '_');
                var themeDir = Path.Combine(entityDir, themeSafe);
                Directory.CreateDirectory(themeDir);

                var tsb = new StringBuilder();
                tsb.AppendLine(CultureInfo.InvariantCulture, $"Тема: {themeGroup.Key}");
                tsb.AppendLine(CultureInfo.InvariantCulture, $"Упоминаний: {themeGroup.Count()}");
                tsb.AppendLine();
                currentFile = null;
                foreach (var (file, mention) in themeGroup)
                {
                    if (!Equals(currentFile, file.FileName))
                    {
                        currentFile = file.FileName;
                        tsb.AppendLine(CultureInfo.InvariantCulture, $"\n--- {currentFile} ---\n");
                    }
                    tsb.AppendLine(CultureInfo.InvariantCulture, $"[{mention.Num}] {mention.Variant} L{mention.Line}");
                    tsb.AppendLine(mention.Context);
                    tsb.AppendLine();
                }
                File.WriteAllText(Path.Combine(themeDir, $"{safe}_{themeSafe}.txt"), tsb.ToString(), new UTF8Encoding(false));
            }
        }
        return baseDir;
    }

    // ---------- variants index ----------

    public static string WriteVariantsIndex(EntityCatalog catalog, string variantsDir)
    {
        Directory.CreateDirectory(variantsDir);
        foreach (var (entity, _) in catalog.VariantsByEntity)
        {
            var safe = SafeName().Replace(entity, "_");
            File.WriteAllText(
                Path.Combine(variantsDir, safe + ".txt"),
                catalog.ExportVariantsText(entity),
                new UTF8Encoding(false));
        }
        return variantsDir;
    }

    internal static string Escape(string s) => s
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;")
        .Replace("\"", "&quot;")   // R1-20: кавычки — защита будущих атрибутных вставок
        .Replace("'", "&#39;");
}

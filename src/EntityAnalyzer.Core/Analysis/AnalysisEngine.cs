using System.Collections.Concurrent;
using System.Text;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Filtering;
using EntityAnalyzer.Core.Fuzzy;
using EntityAnalyzer.Core.IO;
using EntityAnalyzer.Core.Matching;
using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Text;
using EntityAnalyzer.Core.Themes;
using EntityAnalyzer.Core.Variants;

namespace EntityAnalyzer.Core.Analysis;

/// <summary>
/// The analysis pipeline: corpus discovery → parallel per-file processing
/// (automaton match → boundary filters → overlap resolution → context building →
/// listing/common-word/title rules → dedup → fuzzy pass) with progress, cancellation
/// and an optional content-addressed cache.
/// </summary>
public sealed class AnalysisEngine
{
    private readonly AnalyzerOptions _options;
    private readonly EntityCatalog _catalog;
    private readonly string _presetName;
    private readonly SourceQualitySidecar _sourceQualitySidecar;

    static AnalysisEngine()
    {
        // Enable windows-1251 decoding for legacy files.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public AnalysisEngine(AnalyzerOptions options, EntityCatalog catalog, string presetName = "extended")
    {
        _options = options;
        _catalog = catalog;
        _presetName = presetName;
        _definitions = new Dictionary<string, EntityDefinition>(catalog.Definitions, StringComparer.Ordinal);
        _sourceQualitySidecar = LoadSourceQualitySidecar(options);
        if (_sourceQualitySidecar.IsEnabled)
            Logging.Log.Info("source", "Sidecar качества источников: {0} ({1} файлов, sha256 {2})",
                _sourceQualitySidecar.Path ?? "", _sourceQualitySidecar.Count, _sourceQualitySidecar.Sha256 ?? "");
        _cacheDir = SanitizeCacheDir(options.Cache.Dir, options.Output.Dir);
        // R1-13: препроцессоры текста (до нормализации). Неизвестное имя —
        // ошибка конфигурации, а не молчаливое игнорирование.
        var ppResolve = Text.TextPreprocessorRegistry.Resolve(options.Processing.Preprocessors);
        if (!ppResolve.IsOk)
            throw new ArgumentException(ppResolve.Error, nameof(options));
        _preprocessors = ppResolve.Preprocessors!;
        // R1-11: подключаемые модули анализа (семантика, fuzzy-спасение,
        // межфайловый дедуп). Воронка сущностей остаётся в движке.
        _pipeline = new Modules.ModulePipeline(
        [
            new Modules.SemanticPatternsModule(),
            new Modules.FuzzyRescueModule(),
            new Modules.CrossFileDedupModule(),
        ], options);
    }

    private readonly string _cacheDir;
    private readonly Modules.ModulePipeline _pipeline;
    private readonly IReadOnlyList<Text.ITextPreprocessor> _preprocessors;

    /// <summary>
    /// Explicit sidecar path wins; otherwise auto-discover in the output folder.
    /// A missing optional sidecar keeps legacy detector-only behaviour.
    /// </summary>
    private static SourceQualitySidecar LoadSourceQualitySidecar(AnalyzerOptions options)
    {
        var configured = options.SourceQuality.SidecarPath;
        var explicitPath = !string.IsNullOrWhiteSpace(configured);
        var path = explicitPath
            ? Path.GetFullPath(configured)
            : Path.Combine(options.Output.Dir, SourceQualitySidecar.FileName);

        if (!File.Exists(path))
        {
            if (explicitPath || options.SourceQuality.RequireSidecar)
                throw new FileNotFoundException(
                    $"Source-quality sidecar not found: {path}", path);
            return SourceQualitySidecar.Disabled;
        }

        return SourceQualitySidecar.Load(path);
    }

    /// <summary>
    /// Apply the sidecar after cache lookup. Source metadata is file-level and
    /// does not alter mentions, so a sidecar change must not invalidate the
    /// expensive content cache. Unreadable files are never upgraded.
    /// </summary>
    private FileResult ApplySourceQuality(FileResult result, string path)
    {
        if (!_sourceQualitySidecar.IsEnabled || result.Error is not null)
            return result;

        var detector = new SourceQualityResult(result.SourceQuality, result.SourceReason);
        var resolved = _sourceQualitySidecar.Resolve(Path.GetFileName(path), detector);
        return resolved == detector
            ? result
            : result with { SourceQuality = resolved.Quality, SourceReason = resolved.Reason };
    }

    /// <summary>Legacy Android paths («/storage/…») are redirected under the output folder.</summary>
    internal static string SanitizeCacheDir(string dir, string outputDir)
    {
        if (string.IsNullOrWhiteSpace(dir))
            return Path.Combine(outputDir, ".cache");
        if (Path.IsPathRooted(dir) && !System.Text.RegularExpressions.Regex.IsMatch(dir, @"^[A-Za-z]:[\\/]"))
            return Path.Combine(outputDir, ".cache"); // unix-style root on Windows
        return dir;
    }

    /// <summary>
    /// Кэш контентно-адресный: после переименования/перемещения файла
    /// верхнеуровневые FileName/FullPath надо обновить, но и вложенные
    /// SemanticMatch.FilePath — иначе отчёты по паттернам ссылаются на старое
    /// имя. Это же правило использует любой путь возврата из кэша.
    /// </summary>
    internal static FileResult StampFileIdentity(FileResult cached, string path)
    {
        var fileName = Path.GetFileName(path);
        var semantics = cached.SemanticMatches;
        if (semantics.Count == 0)
            return cached with { FileName = fileName, FullPath = path };

        return cached with
        {
            FileName = fileName,
            FullPath = path,
            SemanticMatches = semantics.Select(s => s with { FilePath = path }).ToArray(),
        };
    }

    public AnalysisSession Run(CancellationToken cancellationToken, IProgress<AnalysisProgress>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var startedAt = DateTimeOffset.Now;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var quality = new QualityCollector();

        Logging.Log.Info("engine", "Старт анализа: корпус={0}, пресет={1}", _options.Corpus.Dir, _presetName);

        // Serialize progress reporting — user sinks are not guaranteed thread-safe.
        object progressGate = new();

        void ReportSafe(AnalysisProgress p) { lock (progressGate) progress?.Report(p); }

        var automaton = _catalog.BuildAutomaton();
        var signalWords = _options.Processing.ToSignalWordSet();
        var proc = _options.Processing;
        var listingFilter = new ListingDensityFilter(signalWords,
            proc.EnumerationMaxSegmentRatio, proc.EnumerationMaxSegmentWords);
        var commonWordRule = new CommonWordContextRule(signalWords);
        var titleValidator = new TitleValidator();
        var blacklistFilter = proc.BlacklistPhrasesEnabled
            ? new BlacklistPhraseFilter(proc.ListingBlacklistPhrases)
            : null;
        var meaningFilter = proc.MeaningfulnessEnabled
            ? new MeaningfulnessFilter(proc.MinFunctionWordRatio)
            : null;
        var userExclusions = proc.UserExclusionsEnabled && proc.UserExcludePatterns.Count > 0
            ? new UserExclusionFilter(proc.UserExcludePatterns)
            : null;
        if (userExclusions is not null)
            Logging.Log.Info("user-filters", "Активных пользовательских паттернов: {0}", userExclusions.PatternCount);

        var allNames = _catalog.AllNames.ToList();

        // R1-11: семантические паттерны и fuzzy-спасение живут в модулях;
        // движок только даёт им фазы жизненного цикла.
        _pipeline.OnSessionStarting(new Modules.ModuleSessionContext
        {
            Catalog = _catalog,
            Options = _options,
            Quality = quality,
        });

        var configSignature = BuildConfigSignature();
        IAnalysisCache? cache = null;
        MemoryCacheLayer? l2 = null;
        if (_options.Cache.Enabled)
        {
            // Жизненный цикл (TTL, лимит объёма, осиротевшие файлы) применяется
            // при открытии кэша — один раз на сессию; результат — в журнал.
            var policy = CachePolicy.From(_options.Cache);
            var disk = new JsonFileCache(_cacheDir, configSignature, policy);
            if (disk.LastPruneRemoved > 0)
                Logging.Log.Info("cache", "Кэш: убрано записей {0}, освобождено {1:0.#} МБ",
                    disk.LastPruneRemoved, disk.LastPruneFreedBytes / 1048576.0);

            if (_options.Cache.MemoryEnabled && _options.Cache.MemoryBudgetMb > 0)
            {
                l2 = MemoryCacheLayer.Wrap(_cacheDir, configSignature, disk,
                    _options.Cache.MemoryBudgetMb * 1024L * 1024L);
                cache = l2;
            }
            else
            {
                cache = disk;
            }
        }
        var l2HitsBefore = l2?.HitCount ?? 0;
        var l2MissesBefore = l2?.MissCount ?? 0;

        // Discovery lists paths only; bytes are read inside the parallel loop
        // so disk IO overlaps with CPU work instead of serializing upfront.
        // Выходная папка и кэш исключаются: иначе собственные отчёты попадают
        // в корпус, и каждый следующий прогон «съедает» предыдущие цитаты
        // (QA BUG-002, снежный ком).
        var excludedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void ExcludeTree(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try
            {
                var full = Path.GetFullPath(p);
                excludedRoots.Add(full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar);
            }
            catch { /* некорректный путь — пропускаем */ }
        }
        ExcludeTree(_options.Output.Dir);
        ExcludeTree(_cacheDir);

        var t = DiscoverFiles(cancellationToken, excludedRoots, quality);
        var discovered = t.Item1;
        var largeFiles = t.Item2;
        var totalFiles = discovered.Count + largeFiles.Count;
        Logging.Audit.CorpusScan(_options.Corpus.Dir, totalFiles); // R2-23: аудит скана корпуса
        var processed = 0;
        var seenContentHashes = new ConcurrentDictionary<string, byte>();
        var results = new ConcurrentBag<FileResult>();

        // Дубликаты текстов: карта «хэш → первый путь» + копии. Собирается всегда,
        // независимо от SkipDuplicates — чтобы корпус можно было проверить на повторы.
        var firstPathByHash = new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
        var duplicateHits = new ConcurrentBag<(string Hash, string Path)>();

        // Инкрементальный индекс: неизменённые файлы не читаются с диска.
        var incrementalIndex = proc.IncrementalEnabled && _options.Cache.Enabled
            ? new IncrementalIndexStore(Path.Combine(_cacheDir, "incremental.json"))
            : null;

        ReportSafe(new AnalysisProgress(0, totalFiles, "", "Начато"));
        Logging.Log.Info("engine", "Найдено файлов: {0} (доп. папок корпуса: {1})",
            totalFiles, _options.Corpus.ExtraDirs.Count);

        var parallelOptions = new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = proc.MaxWorkers > 0
                ? proc.MaxWorkers
                : Environment.ProcessorCount
        };

        Parallel.ForEachAsync(discovered, parallelOptions, (path, token) =>
        {
            token.ThrowIfCancellationRequested();

            FileResult? result = null;
            try
            {
                string contentKey;
                byte[]? bytes = null;
                var hashFromIndex = false;
                if (incrementalIndex is not null && incrementalIndex.TryGetHash(path, out var indexedHash))
                {
                    // Хэш из индекса без чтения файла, если размер и mtime не изменились.
                    contentKey = indexedHash;
                    hashFromIndex = true;
                }
                else
                {
                    bytes = File.ReadAllBytes(path);
                    contentKey = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
                    incrementalIndex?.StoreHash(path, contentKey);
                    // Свежий хэш тоже позволяет сразу проверить кэш по контенту
                    // (переименование/перемещение) — как раньше делал GetOrComputeHash.
                    hashFromIndex = incrementalIndex is not null;
                }

                // Регистрация контента для отчёта по дубликатам (до любых ранних выходов).
                // Пропуск дубликатов решается здесь, до кэша: иначе второй файл
                // с тем же содержимым мог получить готовый результат из кэша и
                // обойти счётчик SkippedDuplicate (гонка на холодном кэше и
                // гарантированно — на тёплом).
                var isDuplicate = !firstPathByHash.TryAdd(contentKey, path);
                if (isDuplicate)
                {
                    duplicateHits.Add((contentKey, path));
                    if (_options.Corpus.SkipDuplicates)
                    {
                        quality.CountSkippedDuplicate();
                        return ValueTask.CompletedTask;
                    }
                }

                // Быстрый путь: кэш-попадание по хэшу из индекса — файл не читаем вовсе.
                var cacheKey = contentKey + "|" + configSignature;
                var cacheChecked = false;
                if (hashFromIndex && cache is not null)
                {
                    cacheChecked = true;
                    if (cache.TryGet(cacheKey, out var cachedEarly) && cachedEarly is not null)
                    {
                        quality.CountCacheHit();
                        quality.CountUnchangedSkipped();
                        // R2-5: результат контентно-адресный, а имя/путь могли смениться
                        // (переименование/перемещение) — штампуем актуальную идентификацию.
                        result = StampFileIdentity(cachedEarly, path);
                        quality.CountFinalMentions(result.Mentions.Count);
                    }
                }

                if (result is null)
                {
                    bytes ??= File.ReadAllBytes(path);

                    result = AnalyzeFile(
                        path, bytes, contentKey, automaton,
                        listingFilter, commonWordRule, titleValidator,
                        blacklistFilter, meaningFilter, userExclusions,
                        allNames, cache, quality, configSignature,
                        seenContentHashes, skipCacheLookup: cacheChecked);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logging.Log.Error("file", $"{path}: {ex.Message}");
                quality.AddError(Path.GetFileName(path), ex.Message);
            }
            finally
            {
                // Ровно один отчёт на каждый обнаруженный файл: успешный, дубликат
                // (result == null) или нечитаемый. Иначе прогресс навсегда застревает
                // перед 100% (в логах CLI: «…8725/8735» при полном прогоне).
                var done = Interlocked.Increment(ref processed);
                ReportSafe(new AnalysisProgress(done, totalFiles, Path.GetFileName(path),
                    ElapsedSec: stopwatch.Elapsed.TotalSeconds));
            }

            if (result is not null)
                results.Add(ApplySourceQuality(result, path));
            return ValueTask.CompletedTask;
        }).GetAwaiter().GetResult(); // Run() is synchronous; propagate exceptions incl. OCE

        // R1-5: потоковая обработка больших файлов (StreamLargeFiles).
        // Последовательно, чтобы не перегружать память; quality общий — воронка суммируется.
        foreach (var path in largeFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = AnalyzeLargeFileStreamed(
                path, automaton, listingFilter, commonWordRule, titleValidator,
                blacklistFilter, meaningFilter, userExclusions,
                allNames, quality, configSignature, cancellationToken)
                .GetAwaiter().GetResult();
            if (result is not null)
                results.Add(ApplySourceQuality(result, path));
        }

        incrementalIndex?.Flush();
        cache?.Flush();

        // L2-метрика прогона: дельта счётчиков слоя (память уже прогрета прошлыми
        // прогонами — абсолютные значения к прогону не относятся).
        if (l2 is not null)
        {
            var l2Hits = (int)Math.Max(0, l2.HitCount - l2HitsBefore);
            var l2Misses = (int)Math.Max(0, l2.MissCount - l2MissesBefore);
            quality.SetCacheL2(l2Hits, l2Misses);
            if (l2Hits + l2Misses > 0)
                Logging.Log.Info("cache", "Кэш L2 (память): попаданий {0}, обращений к диску {1}",
                    l2Hits, l2Misses);
        }

        // Primary order is the historical file-name order. FullPath is a
        // deterministic tie-breaker for same-named files from different folders
        // (for example many «Оригинал.txt»), otherwise parallel completion order
        // leaks into semantic reports and makes repeated runs non-identical.
        var orderedResults = results
            .OrderBy(r => r.FileName, StringComparer.Ordinal)
            .ThenBy(r => r.FullPath, StringComparer.Ordinal)
            .ToList();

        // Группы текстовых дубликатов: оригинал + копии с тем же SHA-256.
        var duplicateGroups = BuildDuplicateGroups(firstPathByHash, duplicateHits);
        if (_options.Corpus.SkipDuplicates)
            orderedResults = ReconcileDuplicateRepresentatives(orderedResults, duplicateGroups, firstPathByHash);
        if (duplicateGroups.Count > 0)
            Logging.Log.Info("duplicates",
                $"Текстовых дубликатов: {duplicateGroups.Sum(g => g.RedundantCount)} файлов в {duplicateGroups.Count} группах");

        // Session-level grouping of copied reference blocks: identical contexts
        // across files collapse into the first occurrence + repeat counter.
        // R1-11: выполняется модулем CrossFileDedupModule через общий контракт;
        // модуль отдаёт счётчик для сверки воронки (Quality.Snapshot).
        var sessionCtx = new Modules.SessionModuleContext { Results = orderedResults, Quality = quality };
        _pipeline.OnSessionCompleted(sessionCtx);
        orderedResults = sessionCtx.Results;
        var duplicatesGrouped = sessionCtx.GroupedCount;

        var summary = BuildSummary(orderedResults);
        stopwatch.Stop();

        // R2-14: публикация события завершения прогона — подписчики шины
        // (история, watch, будущие потребители) узнают об итоге без прямой связи.
        Events.EventBus.Default.Publish(new Events.AnalysisCompletedEvent(
            DateTimeOffset.Now, summary.TotalMentions, orderedResults.Count, stopwatch.Elapsed.TotalSeconds));

        // Семантические находки всех файлов в порядок чтения.
        var semanticMatches = orderedResults
            .SelectMany(r => r.SemanticMatches)
            .ToList();

        // Сводка по сработавшим паттернам: совпадения + уникальные файлы.
        var semanticStats = semanticMatches
            .GroupBy(m => m.PatternId, StringComparer.Ordinal)
            .Select(g => new SemanticPatternStat(
                g.Key,
                g.First().PatternName,
                g.Count(),
                g.Select(x => x.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count()))
            .OrderByDescending(s => s.Matches)
            .ToList();

        return new AnalysisSession(
            orderedResults, summary, quality.Snapshot(stopwatch.Elapsed, duplicatesGrouped, semanticStats),
            _presetName, startedAt, stopwatch.Elapsed,
            DuplicateGroups: duplicateGroups,
            SemanticMatches: semanticMatches);
    }

    /// <summary>
    /// A skipped duplicate is represented by the lexicographically first path
    /// in its content group, not by whichever worker won the race. Source
    /// quality is the worst quality of all identical copies: otherwise the
    /// same bytes could enter trusted under a different filename.
    /// </summary>
    private List<FileResult> ReconcileDuplicateRepresentatives(
        List<FileResult> results,
        IReadOnlyList<DuplicateGroup> groups,
        IReadOnlyDictionary<string, string> firstPathByHash)
    {
        if (groups.Count == 0) return results;

        var byPath = results.ToDictionary(r => r.FullPath, StringComparer.OrdinalIgnoreCase);
        var replacements = new Dictionary<string, FileResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in groups)
        {
            if (!firstPathByHash.TryGetValue(group.Hash, out var keptPath) ||
                !byPath.TryGetValue(keptPath, out var kept))
                continue;

            var representative = group.Files[0];
            var adjusted = string.Equals(kept.FullPath, representative, StringComparison.OrdinalIgnoreCase)
                ? kept
                : StampFileIdentity(kept, representative);

            var worst = WorstSidecarQuality(group.Files);
            if (worst is { } source)
                adjusted = adjusted with { SourceQuality = source.Quality, SourceReason = source.Reason };
            replacements[keptPath] = adjusted;
        }

        return results
            .Select(r => replacements.TryGetValue(r.FullPath, out var replacement) ? replacement : r)
            .OrderBy(r => r.FileName, StringComparer.Ordinal)
            .ThenBy(r => r.FullPath, StringComparer.Ordinal)
            .ToList();
    }

    private SourceQualityResult? WorstSidecarQuality(IReadOnlyList<string> paths)
    {
        if (!_sourceQualitySidecar.IsEnabled) return null;

        SourceQualityResult? worst = null;
        var worstRank = -1;
        foreach (var path in paths)
        {
            var resolved = _sourceQualitySidecar.Resolve(
                Path.GetFileName(path),
                new SourceQualityResult(SourceQuality.Usable, SourceQuality.ReasonDetectorNegative));
            var rank = resolved.Quality switch
            {
                SourceQuality.DegradedLexical => 3,
                SourceQuality.DegradedOcr => 2,
                SourceQuality.Unknown => 1,
                _ => 0,
            };
            if (rank <= worstRank) continue;
            worstRank = rank;
            worst = resolved;
        }
        return worstRank > 0 ? worst : null;
    }

    /// <summary>Сборка групп дубликатов из карты первых путей и списка копий.</summary>
    internal static List<DuplicateGroup> BuildDuplicateGroups(
        IReadOnlyDictionary<string, string> firstPathByHash,
        IEnumerable<(string Hash, string Path)> duplicateHits)
    {
        return duplicateHits
            .GroupBy(d => d.Hash, StringComparer.Ordinal)
            .Select(g => new DuplicateGroup(
                g.Key,
                new[] { firstPathByHash[g.Key] }
                    .Concat(g.Select(d => d.Path))
                    // The representative must not depend on the parallel
                    // completion order: the same corpus must always name the
                    // same original and attach the same rows to it.
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(p => p, StringComparer.Ordinal)
                    .ToList()))
            .OrderBy(g => g.Files[0], StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Files[0], StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Обособленный скан семантических паттернов: без поиска сущностей,
    /// фильтров и кэша — только конструкции, заданные паттернами.
    /// Используется кнопкой «Искать только паттерны» и CLI --patterns-only.
    /// </summary>
    public PatternScanResult ScanPatternsOnly(
        CancellationToken ct = default, IProgress<AnalysisProgress>? progress = null)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Исключения выходной папки и кэша — как в полном прогоне (QA BUG-002).
        var excludedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void ExcludeTree(string? p)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            try
            {
                var full = Path.GetFullPath(p);
                excludedRoots.Add(full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar);
            }
            catch { /* некорректный путь — пропускаем */ }
        }
        ExcludeTree(_options.Output.Dir);
        ExcludeTree(_cacheDir);

        var files = DiscoverFiles(ct, excludedRoots).Item1;
        var total = files.Count;

        var store = Semantics.SemanticPatternStore.Load();
        var matcher = new Semantics.SemanticMatcher(
            store.Patterns, store.Lexicons, Enumerable.Empty<string>(),
            _options.Processing.SemanticMaxPerPatternPerFile);

        var matches = new ConcurrentBag<SemanticMatch>();
        var processed = 0;
        var emptyFiles = 0;

        Parallel.ForEach(files,
            new ParallelOptions { CancellationToken = ct },
            path =>
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var bytes = File.ReadAllBytes(path);
                    if (bytes.Length == 0) { Interlocked.Increment(ref emptyFiles); return; }
                    var text = IO.DocumentReader.Extract(path, bytes);
                    if (text.Length == 0) { Interlocked.Increment(ref emptyFiles); return; }
                    // R1-13: препроцессоры — до нормализации (колонтитулы,
                    // повторы шапок). Чистые функции, порядок из конфигурации.
                    foreach (var pp in _preprocessors)
                        text = pp.Process(text, path);
                    if (text.Length == 0) { Interlocked.Increment(ref emptyFiles); return; }
                    var normalized = Text.TextNormalizer.Normalize(text);
                    if (normalized.Length == 0) { Interlocked.Increment(ref emptyFiles); return; }
                    var lineStarts = BuildLineStarts(normalized);
                    foreach (var m in matcher.Scan(normalized, path, lineStarts))
                        matches.Add(m);
                }
                catch { Interlocked.Increment(ref emptyFiles); }

                var done = Interlocked.Increment(ref processed);
                if (progress is not null && (done % 50 == 0 || done == total))
                    progress.Report(new AnalysisProgress(done, total, path,
                        ElapsedSec: sw.Elapsed.TotalSeconds));
            });

        sw.Stop();
        Logging.Log.Info("semantic",
            $"Обособленный скан паттернов: файлов {total}, находок {matches.Count}, " +
            $"за {sw.Elapsed.TotalSeconds:0.0}s");

        return new PatternScanResult(
            [.. matches.OrderBy(m => m.PatternId, StringComparer.Ordinal)
                       .ThenBy(m => m.FilePath, StringComparer.OrdinalIgnoreCase)
                       .ThenBy(m => m.Line)],
            total, emptyFiles, sw.Elapsed);
    }

    private static double FilesPerSec(int done, double seconds) =>
        seconds > 0.5 ? done / seconds : 0;

    private static double EtaSeconds(int done, int total, double seconds) =>
        done > 0 && seconds > 0.5 ? (total - done) / (done / seconds) : 0;

    // ---------- discovery ----------

    private Tuple<List<string>, List<string>> DiscoverFiles(CancellationToken ct, HashSet<string>? excludedRoots = null,
        QualityCollector? quality = null)
    {
        var extensions = _options.Corpus.Extensions
            .Select(e => e.StartsWith('.') ? e : "." + e)
            .Select(e => e.ToLowerInvariant())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var maxBytes = Math.Max(1, _options.Corpus.MaxFileSizeMb) * 1024L * 1024L;
        var searchOption = _options.Corpus.Recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;

        // Виртуальный корпус: основная папка + дополнительные.
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(_options.Corpus.Dir) && Directory.Exists(_options.Corpus.Dir))
            roots.Add(_options.Corpus.Dir);
        foreach (var extra in _options.Corpus.ExtraDirs)
            if (!string.IsNullOrWhiteSpace(extra) && Directory.Exists(extra))
                roots.Add(extra);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        var largeFiles = new List<string>();

        foreach (var dir in roots)
            foreach (var path in Directory.EnumerateFiles(dir, "*", searchOption))
            {
                ct.ThrowIfCancellationRequested();
                if (!extensions.Contains(Path.GetExtension(path))) continue;
                if (!seen.Add(path)) continue;
                try
                {
                    var info = new FileInfo(path);
                    if (!info.Exists || info.Length == 0)
                    {
                        if (info.Exists) quality?.CountSkippedEmpty();
                        continue;
                    }
                    if (info.Length > maxBytes)
                    {
                        if (_options.Corpus.StreamLargeFiles)
                            largeFiles.Add(path);
                        else
                            quality?.CountSkippedTooLarge();
                        continue;
                    }

                    // Файлы внутри выходной папки/кэша не сканируются.
                    if (excludedRoots is { Count: > 0 })
                    {
                        var full = Path.GetFullPath(path);
                        var isExcluded = false;
                        foreach (var root in excludedRoots)
                            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                            {
                                isExcluded = true;
                                break;
                            }
                        if (isExcluded) continue;
                    }

                    list.Add(path);
                }
                catch { /* unreadable file — skip */ }
            }

        return Tuple.Create(
            list
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            largeFiles
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList());
    }

    // ---------- per-file pipeline ----------

    private FileResult? AnalyzeFile(
        string path, byte[] bytes, string contentKey, AhoCorasick<EntityRef> automaton,
        ListingDensityFilter listingFilter, CommonWordContextRule commonWordRule,
        TitleValidator titleValidator,
        BlacklistPhraseFilter? blacklistFilter, MeaningfulnessFilter? meaningFilter,
        UserExclusionFilter? userExclusions,
        List<string> allNames,
        IAnalysisCache? cache,
        QualityCollector quality, string configSignature,
        ConcurrentDictionary<string, byte> seenHashes,
        bool skipCacheLookup = false)
    {
        var fileName = Path.GetFileName(path);
        var proc = _options.Processing;
        try
        {
            // Duplicate content guard. Хэш уже посчитан в Run(): не читаем и не
            // хешируем те же байты повторно.
            if (_options.Corpus.SkipDuplicates && !seenHashes.TryAdd(contentKey, 0))
            {
                quality.CountSkippedDuplicate();
                return null;
            }

            var cacheKey = contentKey + "|" + configSignature;
            if (cache is not null && skipCacheLookup)
            {
                // Ранний путь в Run() уже проверил диск и не нашёл записи.
                quality.CountCacheMiss();
            }
            else if (cache is not null && cache.TryGet(cacheKey, out var cached))
            {
                quality.CountCacheHit();
                var reused = StampFileIdentity(cached!, path);
                quality.CountFinalMentions(reused.Mentions.Count);
                return reused;
            }
            else if (cache is not null)
            {
                quality.CountCacheMiss();
            }

            string text;
            using (quality.Measure("read"))
            {
                text = DocumentReader.Extract(path, bytes);
                quality.CountEncoding(isUtf8: HasUtf8BomOrValid(bytes), bytes.LongLength);
            }
            foreach (var pp in _preprocessors)
                text = pp.Process(text, path);

            string normalized;
            using (quality.Measure("normalize"))
                normalized = TextNormalizer.Normalize(text);
            var charCount = (long)normalized.Length;
            var lineStarts = BuildLineStarts(normalized);

            var (result, _, _, _, _) = ProcessNormalizedText(
                normalized, lineStarts, charCount,
                contentKey, cacheKey, cache,
                quality, configSignature,
                seenHashes,
                0, // lineOffset
                fileName, path,
                automaton, listingFilter, commonWordRule, titleValidator,
                blacklistFilter, meaningFilter, userExclusions, allNames);
            return result;
        }
        catch (Exception ex)
        {
            quality.AddError(fileName, ex.Message);
            return new FileResult(fileName, path, [], 0, 0, 0, 0, Error: ex.Message);
        }
    }

    /// <summary>
    /// Ядро пайплайна: от нормализованного текста до FileResult.
    /// Используется AnalyzeFile (lineOffset=0) и AnalyzeLargeFileStreamed (lineOffset>0).
    /// </summary>
    private (FileResult Result, HashSet<string> FoundEntities, List<SemanticMatch> SemanticMatches, int TotalFound, long CharCount) ProcessNormalizedText(
        string normalized, int[] lineStarts, long charCount,
        string contentKey, string? cacheKey, IAnalysisCache? cache,
        QualityCollector quality, string configSignature,
        ConcurrentDictionary<string, byte>? seenHashes,
        int lineOffset,
        string fileName, string path,
        AhoCorasick<EntityRef> automaton,
        ListingDensityFilter listingFilter, CommonWordContextRule commonWordRule,
        TitleValidator titleValidator,
        BlacklistPhraseFilter? blacklistFilter, MeaningfulnessFilter? meaningFilter,
        UserExclusionFilter? userExclusions,
        List<string> allNames)
    {
        var proc = _options.Processing;
        var mentions = new List<Mention>();
        var foundEntities = new HashSet<string>();
        var moduleCtx = new Modules.FileModuleContext
        {
            Path = path,
            NormalizedText = normalized,
            LineStarts = lineStarts,
            Quality = quality,
            Mentions = mentions,
            FoundEntities = foundEntities,
        };

        // Raw automaton hits.
        var raw = new List<RawMatch>();
        using (quality.Measure("match"))
        {
            automaton.Find(normalized,
                hit => raw.Add(new RawMatch(hit.Start, hit.End, hit.Payload.Variant, hit.Payload.Entity)));
        }
        quality.CountRawMatches(raw.Count);

        // Boundary filters.
        var filtered = new List<RawMatch>(raw.Count);
        using (quality.Measure("filter"))
        {
            foreach (var m in raw)
            {
                if (proc.BoundaryFiltersEnabled)
                {
                    if (MatchFilters.IsFalsePositive(normalized, m.Start, m.End)) { quality.CountBoundaryRejected(); continue; }
                    if (MatchFilters.IsBrokenWord(normalized, m.Start, m.End)) { quality.CountBrokenWordRejected(); continue; }
                }
                filtered.Add(m);
            }
        }

        var resolved = MatchResolver.ResolveOverlaps(filtered);
        quality.CountOverlapsResolved(filtered.Count - resolved.Count);
        var totalFound = resolved.Count;
        var sourceQuality = SourceQuality.Classify(normalized);

        // R1-11: семантические паттерны — фаза ProcessFile конвейера модулей.
        _pipeline.ProcessFile(moduleCtx);
        var semanticMatches = moduleCtx.SemanticMatches;

        if (totalFound == 0)
        {
            var empty = new FileResult(fileName, path, [], 0, 0, charCount, 0,
                SemanticMatches: semanticMatches)
            {
                SourceQuality = sourceQuality.Quality,
                SourceReason = sourceQuality.Reason,
            };
            cache?.Set(cacheKey!, empty);
            return (empty, foundEntities, semanticMatches, 0, charCount);
        }

        var ctxSize = ContextBuilder.CalculateContextSize(
            totalFound, proc.ContextMaxWords, proc.ContextMinWords, proc.ContextThreshold);

        var selected = SelectEvenly(resolved, proc.MaxMentionsPerFile);
        quality.CountDisplayCapped(Math.Max(0, totalFound - selected.Count));

        List<SentenceSpan> spans;
        using (quality.Measure("split"))
            spans = SentenceSplitter.SplitWithSpans(normalized);

        int lastSpan = -1;
        string cachedContext = "";
        using (quality.Measure("context"))
        foreach (var m in selected)
        {
            var si = FindSpanIndex(spans, m.Start);
            if (si != lastSpan)
            {
                cachedContext = ContextBuilder.Build(normalized, spans, m.Start, m.End, ctxSize);
                lastSpan = si;
            }
            var context = cachedContext;
            var line = CountLineByIndex(lineStarts, m.Start) + lineOffset;
            var variant = m.Variant;

            _definitions.TryGetValue(m.Entity, out var def);
            if (proc.TitleValidationEnabled &&
                def is not null && def.Titles.Contains(variant, StringComparer.Ordinal))
            {
                var markers = def.Irregular.Concat(def.Titles).Append(def.Canon).ToList();
                if (!titleValidator.IsValid(context, m.Entity, variant, markers))
                {
                    quality.CountTitleRejected();
                    continue;
                }
            }

            if (proc.ListingDensityEnabled && listingFilter.IsListing(context, m.Entity, allNames))
            {
                quality.CountListingRejected();
                continue;
            }

            if (blacklistFilter is not null && blacklistFilter.IsBlacklisted(context))
            {
                quality.CountBlacklistRejected();
                continue;
            }

            if (meaningFilter is not null && meaningFilter.IsMeaningless(context))
            {
                quality.CountMeaningfulnessRejected();
                continue;
            }

            if (userExclusions is not null && userExclusions.IsExcluded(context))
            {
                quality.CountUserPatternRejected();
                continue;
            }

            if (proc.CommonWordRuleEnabled &&
                !commonWordRule.Allows(context, m.Entity, _catalog.IsCommonWordEntity(m.Entity)))
            {
                quality.CountCommonWordRejected();
                continue;
            }

            mentions.Add(new Mention(mentions.Count + 1, m.Entity, variant, line,
                context, ThemeClassifier.Classify(context), Offset: m.Start)); // R2-13
            foundEntities.Add(m.Entity);
        }

        var beforeDedup = mentions.Count;
        var deduped = ContextBuilder.Deduplicate(mentions, x => x.Context, x => x.Entity)
            .Select((m, i) => m with { Num = i + 1 })
            .ToList();
        mentions.Clear();
        mentions.AddRange(deduped);
        quality.CountDeduplicated(beforeDedup - mentions.Count);

        // R1-11: fuzzy-спасение — фаза EnrichFile.
        _pipeline.EnrichFile(moduleCtx);

        quality.CountFinalMentions(mentions.Count);
        var density = charCount > 0 ? Math.Round(totalFound * 10000.0 / charCount, 2) : 0;

        var result = new FileResult(fileName, path, mentions, totalFound, ctxSize, charCount, density,
            SemanticMatches: semanticMatches)
        {
            SourceQuality = sourceQuality.Quality,
            SourceReason = sourceQuality.Reason,
        };
        cache?.Set(cacheKey!, result);
        return (result, foundEntities, semanticMatches, totalFound, charCount);
    }

    /// <summary>
    /// R1-5: потоковая обработка файлов, превышающих MaxFileSizeMb.
    /// Файл читается чанками предложений (StreamedSentenceReader), каждый чанк
    /// анализируется через ProcessNormalizedText с lineOffset = chunk.StartLine - 1,
    /// результаты мерджатся в один FileResult. Кэш и препроцессоры для чанков отключены.
    /// </summary>
    private async Task<FileResult?> AnalyzeLargeFileStreamed(
        string path, AhoCorasick<EntityRef> automaton,
        ListingDensityFilter listingFilter, CommonWordContextRule commonWordRule,
        TitleValidator titleValidator,
        BlacklistPhraseFilter? blacklistFilter, MeaningfulnessFilter? meaningFilter,
        UserExclusionFilter? userExclusions,
        List<string> allNames,
        QualityCollector quality, string configSignature,
        CancellationToken ct)
    {
        var fileName = Path.GetFileName(path);
        var proc = _options.Processing;

        // SHA256 всего файла потоково — для дедупа и contentKey.
        string contentKey;
        using (var sha = System.Security.Cryptography.SHA256.Create())
        using (var fs = File.OpenRead(path))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await fs.ReadAsync(buffer, ct)) > 0)
                sha.TransformBlock(buffer, 0, read, null, 0);
            sha.TransformFinalBlock([], 0, 0);
            contentKey = Convert.ToHexString(sha.Hash!);
        }

        if (_options.Corpus.SkipDuplicates)
        {
            // seenHashes — общий для всего прогона; используем пустой словарь,
            // так как дедуп по полному файлу уже проверен выше в Run().
            var dummySeen = new ConcurrentDictionary<string, byte>();
            if (!dummySeen.TryAdd(contentKey, 0))
            {
                quality.CountSkippedDuplicate();
                return null;
            }
        }

        var allMentions = new List<Mention>();
        var allFoundEntities = new HashSet<string>();
        var allSemanticMatches = new List<SemanticMatch>();
        var totalFound = 0;
        long totalCharCount = 0;

        await foreach (var chunk in StreamedSentenceReader.ReadChunksAsync(path, 500, ct))
        {
            ct.ThrowIfCancellationRequested();
            var chunkText = string.Join(" ", chunk.Sentences);
            var normalized = TextNormalizer.Normalize(chunkText); // препроцессоры НЕ применяем
            if (normalized.Length == 0) continue;
            var lineStarts = BuildLineStarts(normalized);
            var charCount = (long)normalized.Length;
            totalCharCount += charCount;

            var (result, foundEntities, semanticMatches, found, _) = ProcessNormalizedText(
                normalized, lineStarts, charCount,
                contentKey, cacheKey: null, cache: null,
                quality, configSignature,
                seenHashes: null,
                chunk.StartLine - 1, // lineOffset
                fileName, path,
                automaton, listingFilter, commonWordRule, titleValidator,
                blacklistFilter, meaningFilter, userExclusions, allNames);

            allMentions.AddRange(result.Mentions);
            allFoundEntities.UnionWith(foundEntities);
            allSemanticMatches.AddRange(semanticMatches);
            totalFound += found;
        }

        if (totalFound == 0)
        {
            var empty = new FileResult(fileName, path, [], 0, 0, totalCharCount, 0,
                SemanticMatches: allSemanticMatches);
            return empty;
        }

        // Ренумеруем упоминания после мерджа.
        for (var i = 0; i < allMentions.Count; i++)
            allMentions[i] = allMentions[i] with { Num = i + 1 };

        var density = totalCharCount > 0 ? Math.Round(totalFound * 10000.0 / totalCharCount, 2) : 0;
        return new FileResult(fileName, path, allMentions, totalFound, 0, totalCharCount, density,
            SemanticMatches: allSemanticMatches);
    }

    // ---------- helpers ----------

    internal static string Decode(byte[] bytes)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
    }

    /// <summary>Грубая классификация кодировки для статистики корпуса.</summary>
    private static bool HasUtf8BomOrValid(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return true;
        try
        {
            // Валидация без материализации строки и обратного кодирования.
            new UTF8Encoding(false, true).GetCharCount(bytes);
            return true;
        }
        catch (DecoderFallbackException) { return false; }
    }

    internal static int CountLine(string text, int position)
    {
        var line = 1;
        for (var i = 0; i < position && i < text.Length; i++)
            if (text[i] == '\n') line++;
        return line;
    }

    /// <summary>Offsets where each line begins (index 0 → offset 0). Built once per file.</summary>
    internal static int[] BuildLineStarts(string text)
    {
        var starts = new List<int>(256) { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') starts.Add(i + 1);
        return [.. starts];
    }

    /// <summary>1-based line number via binary search over the line-start index.</summary>
    internal static int CountLineByIndex(int[] lineStarts, int position)
    {
        var lo = 0;
        var hi = lineStarts.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (lineStarts[mid] <= position) lo = mid;
            else hi = mid - 1;
        }
        return lo + 1;
    }

    /// <summary>
    /// BUG-N10: индекс предложения, содержащего позицию (бинарный поиск по спанам).
    /// Для позиции вне спанов возвращается ближайший предыдущий.
    /// </summary>
    internal static int FindSpanIndex(IReadOnlyList<SentenceSpan> spans, int position)
    {
        var lo = 0;
        var hi = spans.Count - 1;
        var result = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (spans[mid].Start <= position)
            {
                result = mid;
                lo = mid + 1;
            }
            else hi = mid - 1;
        }
        return result;
    }

    internal static IReadOnlyList<RawMatch> SelectEvenly(List<RawMatch> matches, int maxMentions)
    {
        if (maxMentions <= 0 || matches.Count <= maxMentions) return matches;
        var step = matches.Count / (double)maxMentions;
        var picked = new HashSet<int>();
        var result = new List<RawMatch>(maxMentions);
        for (var i = 0; i < maxMentions; i++)
        {
            var idx = (int)(i * step);
            while (!picked.Add(idx)) idx++;
            if (idx >= matches.Count) break;
            result.Add(matches[idx]);
        }
        return result;
    }

    private static string WhitespaceCollapse(string s) =>
        ContextBuilder.CollapseWhitespace(s);

    private readonly Dictionary<string, EntityDefinition> _definitions;

    private string BuildConfigSignature() => BuildSignatureCore();

    /// <summary>Тестовый доступ к подписи конфигурации.</summary>
    public string BuildConfigSignatureForTests() => BuildSignatureCore();

    private string BuildSignatureCore()
    {
        var variants = _catalog.VariantsByEntity
            .OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .SelectMany(kv => kv.Value.Select(v => kv.Key + "=" + v))
            .ToList();
        variants.Sort(StringComparer.Ordinal);

        var p = _options.Processing;
        var payload =
            $"{_presetName}|{p.ContextMaxWords}|{p.ContextMinWords}|{p.ContextThreshold}|" +
            $"{p.MaxMentionsPerFile}|{p.FuzzyEnabled}|{p.DeepCorrupt}|{p.FuzzyThreshold}|" +
            $"F:{p.BoundaryFiltersEnabled}:{p.ListingDensityEnabled}:{p.BlacklistPhrasesEnabled}:" +
            $"{p.MeaningfulnessEnabled}:{p.CommonWordRuleEnabled}:{p.TitleValidationEnabled}:" +
            $"{p.CrossFileDeduplication}:{p.EnumerationMaxSegmentRatio:0.00}:{p.MinFunctionWordRatio:0.00}|" +
            string.Join(';', variants);

        if (p.ListingBlacklistPhrases.Count > 0)
        {
            var bl = string.Join("\u0001", p.ListingBlacklistPhrases);
            payload += "|BL:" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(bl)))[..12];
        }

        if (p.UserExcludePatterns.Count > 0)
        {
            var ue = string.Join("\u0001", p.UserExcludePatterns);
            payload += "|UE:" + Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(ue)))[..12];
        }

        // R1-11: вклады модулей (семантические паттерны дают «|SEM:…»)
        // добавляются в том же порядке и в том же формате, что прежде.
        var signatureBuilder = new StringBuilder(payload);
        // R1-13: включённые препроцессоры меняют текст до поиска — кэш
        // обязан инвалидоваться. Пустой список подпись не меняет (паритет).
        if (_preprocessors.Count > 0)
            signatureBuilder.Append("|PP:").Append(string.Join(',',
                _preprocessors.Select(x => x.Name)));
        _pipeline.AppendConfigSignature(signatureBuilder);

        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(signatureBuilder.ToString())))[..24];
    }

    /// <summary>Отпечаток токена для сигнатуры кэша (включая вложенные опции).</summary>
    private static string TokenFingerprint(Core.Semantics.SemanticToken t) =>
        t.Kind + ":" + t.Value + (t.Required ? "" : "?") +
        (t.Options is { Count: > 0 }
            ? "(" + string.Join("|", t.Options.Select(TokenFingerprint)) + ")"
            : "");

    private AnalysisSummary BuildSummary(List<FileResult> results)
    {
        var counts = results
            .SelectMany(r => r.Mentions)
            .GroupBy(m => m.Entity, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var groups = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var kv in counts)
        {
            var group = _definitions.TryGetValue(kv.Key, out var def) && !string.IsNullOrEmpty(def.Group)
                ? def.Group
                : "Без группы";
            groups[group] = groups.GetValueOrDefault(group) + kv.Value;
        }

        return new AnalysisSummary(
            counts.Values.Sum(),
            results.Count(r => r.Mentions.Count > 0),
            counts.Count,
            results.Sum(r => r.CharCount),
            counts,
            groups);
    }
}

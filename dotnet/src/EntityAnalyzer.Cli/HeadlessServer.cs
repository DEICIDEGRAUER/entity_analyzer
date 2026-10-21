using System.Net;
using System.Text;
using EntityAnalyzer.Core.Analysis;
using EntityAnalyzer.Core.Configuration;
using EntityAnalyzer.Core.Codex;
using EntityAnalyzer.Core.Headless;
using EntityAnalyzer.Core.Persistence;
using EntityAnalyzer.Core.Text;
using EntityAnalyzer.Core.Variants;

namespace EntityAnalyzer.Cli;

/// <summary>
/// R1-12: минимальный HTTP-хост поверх ядра для удалённого анализа больших
/// корпусов (без внешних зависимостей — System.Net.HttpListener).
///
/// Маршруты:
///   GET  /health   → {"ok":true,"version":"…"}
///   POST /analyze  → тело HeadlessRequest (JSON) → прогон → HeadlessResponse
///
/// MVP-ограничения (осознанные): запросы обрабатываются последовательно —
/// большие корпуса всё равно долгие; по умолчанию привязка к localhost.
/// </summary>
public static class HeadlessServer
{
    public static int Serve(string host, int port, CancellationToken cancellationToken = default)
        => ServeAsync(host, port, cancellationToken).GetAwaiter().GetResult();

    public static async Task<int> ServeAsync(string host, int port, CancellationToken cancellationToken)
    {
        var prefix = $"http://{host}:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Console.Error.WriteLine($"Не удалось занять адрес {prefix}: {ex.Message}. " +
                                    "Попробуйте другой --port или запустите от администратора.");
            return 2;
        }

        Console.WriteLine($"Headless API слушает {prefix}");
        Console.WriteLine("Маршруты: GET /health · POST /analyze (JSON: corpus, preset, out, extraDirs, excludes, maxMentions, patternsOnly, formats)");
        Console.WriteLine("Остановка: Ctrl+C");

        try
        {
            while (true)
            {
                HttpListenerContext ctx;
                try
                {
                    // Асинхронное ожидание с привязкой к токену: синхронный
                    // GetContext() не пробуждается Stop() на всех платформах
                    // (интеграционный тест поймал зависание shutdown).
                    ctx = await listener.GetContextAsync().WaitAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break; // listener закрыт извне
                }

                try { Handle(ctx); }
                catch (Exception ex)
                {
                    // Хост не должен умирать от ошибки одного запроса.
                    Console.Error.WriteLine($"Ошибка обработки запроса: {ex.Message}");
                    try { WriteJson(ctx.Response, 500, HeadlessProtocol.ToJson(HeadlessResponse.Fail(ex.Message))); }
                    catch { /* ответ уже ушёл */ }
                }
            }
        }
        finally
        {
            listener.Close();
        }
        return 0;
    }

    private static void Handle(HttpListenerContext ctx)
    {
        var route = ctx.Request.Url?.AbsolutePath.TrimEnd('/') ?? "/";
        switch (ctx.Request.HttpMethod)
        {
            case "GET" when route is "/health" or "":
                WriteJson(ctx.Response, 200,
                    $$"""{"ok":true,"version":"{{Core.About.Version}}","service":"entity-analyzer-headless"}""");
                break;

            case "POST" when route == "/analyze":
                HandleAnalyze(ctx);
                break;

            default:
                WriteJson(ctx.Response, 404, """{"ok":false,"error":"Маршрут не найден. Доступны GET /health и POST /analyze."}""");
                break;
        }
    }

    private static void HandleAnalyze(HttpListenerContext ctx)
    {
        string body;
        using (var reader = new StreamReader(ctx.Request.InputStream, Encoding.UTF8))
            body = reader.ReadToEnd();

        var parsed = HeadlessProtocol.ParseRequest(body);
        if (!parsed.IsOk)
        {
            WriteJson(ctx.Response, 400, HeadlessProtocol.ToJson(HeadlessResponse.Fail(parsed.Error!)));
            return;
        }

        var request = parsed.Request!;
        if (!Directory.Exists(request.Corpus))
        {
            WriteJson(ctx.Response, 400, HeadlessProtocol.ToJson(
                HeadlessResponse.Fail($"Корпус не найден: {request.Corpus}")));
            return;
        }

        var clock = System.Diagnostics.Stopwatch.StartNew();
        var options = new AnalyzerOptions
        {
            Corpus = { Dir = Path.GetFullPath(request.Corpus) },
            Output = { Dir = Path.GetFullPath(request.Out ?? Path.Combine(request.Corpus, "_out")) },
            Cache = { Dir = Path.Combine(Path.GetFullPath(request.Out ?? Path.Combine(request.Corpus, "_out")), ".cache") },
        };
        var presetDef = Presets.Resolve(request.Preset, options);
        Presets.Apply(presetDef, options.Processing);
        if (request.MaxMentions is { } mm) options.Processing.MaxMentionsPerFile = mm;
        options.Corpus.ExtraDirs = [.. request.ExtraDirs];
        options.Processing.UserExcludePatterns = [.. request.Excludes];
        // R1-13: неизвестное имя препроцессора → 400 с сообщением реестра.
        var ppResolve = EntityAnalyzer.Core.Text.TextPreprocessorRegistry.Resolve(request.Preprocessors);
        if (!ppResolve.IsOk)
        {
            WriteJson(ctx.Response, 400, HeadlessProtocol.ToJson(HeadlessResponse.Fail(ppResolve.Error!)));
            return;
        }
        options.Processing.Preprocessors = [.. request.Preprocessors];

        var codex = DefaultCodex.Load();
        var catalog = new EntityCatalog(CodexSearchAdapter.ToDefinitions(codex).ToList(), generateVariants: true);
        var engine = new AnalysisEngine(options, catalog, presetDef.Name);

        if (request.PatternsOnly)
        {
            var scan = engine.ScanPatternsOnly(CancellationToken.None);
            clock.Stop();
            Directory.CreateDirectory(options.Output.Dir);
            ReportExporter.WritePerPatternFolders(scan.Matches,
                Path.Combine(options.Output.Dir, "by_pattern"), request.Formats.ToHashSet(StringComparer.OrdinalIgnoreCase));
            WriteJson(ctx.Response, 200, HeadlessProtocol.ToJson(new HeadlessResponse(
                Ok: true, TotalMentions: scan.Matches.Count, FilesTotal: scan.FilesScanned,
                FilesWithMentions: scan.Matches.Select(m => m.FilePath).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                DurationMs: clock.Elapsed.TotalMilliseconds, ReportsDir: options.Output.Dir,
                EntityCounts: null, Funnel: null, Error: null)));
            return;
        }

        var session = engine.Run(CancellationToken.None);
        clock.Stop();
        ReportExporter.ExportAll(session, options.Output.Dir, request.Formats.ToHashSet(StringComparer.OrdinalIgnoreCase),
            perEntityFolders: options.Output.PerEntityFolders, catalog: catalog, options: options, codex: codex);

        var q = session.Quality;
        WriteJson(ctx.Response, 200, HeadlessProtocol.ToJson(new HeadlessResponse(
            Ok: true,
            TotalMentions: session.Summary.TotalMentions,
            FilesTotal: session.Files.Count,
            FilesWithMentions: session.Summary.FilesWithMentions,
            DurationMs: clock.Elapsed.TotalMilliseconds,
            ReportsDir: options.Output.Dir,
            EntityCounts: session.Summary.EntityCounts.ToDictionary(kv => kv.Key, kv => (int)kv.Value),
            Funnel: new HeadlessFunnel((int)q.RawMatches, q.TotalFilterRejected, q.OverlapsResolved,
                q.DisplayCapped, q.Deduplicated, q.DuplicatesGrouped, q.FinalMentions),
            Error: null)));
    }

    private static void WriteJson(HttpListenerResponse response, int status, string json)
    {
        response.StatusCode = status;
        response.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(json);
        response.ContentLength64 = bytes.Length;
        response.OutputStream.Write(bytes);
        response.OutputStream.Close();
    }
}

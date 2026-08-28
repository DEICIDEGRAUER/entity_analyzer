using System.Collections.Concurrent;
using System.Text;

namespace EntityAnalyzer.Core.Logging;

public enum LogLevel { Debug = 0, Info = 1, Warn = 2, Error = 3 }

/// <summary>
/// Структурированный логгер: файл app-YYYYMMDD.log в каталоге логов,
/// потокобезопасная запись, кольцевой буфер последних записей для GUI,
/// ротация по дате и чистка файлов старше 14 дней.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<(string Level, string Category, string Message)> Ring = new();
    private static string? _dir;
    private static LogLevel _min = LogLevel.Info;
    private static string? _currentDate;
    private static StreamWriter? _writer;
    private const int RingCapacity = 500;
    private const int RetentionDays = 14;

    public static void Init(string dir, LogLevel minLevel)
    {
        lock (Gate)
        {
            try { _writer?.Dispose(); } catch { }
            _writer = null;
            _currentDate = null;
            _dir = dir;
            _min = minLevel;
            try
            {
                Directory.CreateDirectory(dir);
                CleanOldLogs(dir);
            }
            catch { /* логирование не должно падать */ }
        }
    }

    public static void Debug(string category, string message) => Write(LogLevel.Debug, category, message);
    public static void Info(string category, string message) => Write(LogLevel.Info, category, message);
    public static void Info(string category, string fmt, params object[] args) =>
        Write(LogLevel.Info, category, SafeFormat(fmt, args));
    public static void Warn(string category, string message) => Write(LogLevel.Warn, category, message);
    public static void Error(string category, string message) => Write(LogLevel.Error, category, message);
    public static void Error(string category, Exception ex) =>
        Write(LogLevel.Error, category, FormatException(ex));

    /// <summary>
    /// Полная цепочка исключения: тип, сообщение, стек и все внутренние исключения.
    /// Без этого XamlParseException маскирует настоящую причину («ресурс не найден»).
    /// </summary>
    internal static string FormatException(Exception ex)
    {
        var sb = new StringBuilder();
        for (var e = (Exception?)ex; e is not null; e = e.InnerException)
        {
            if (sb.Length > 0) sb.AppendLine().Append("─── inner exception ───").AppendLine();
            sb.Append(e.GetType().Name).Append(": ").AppendLine(e.Message);
            if (e.StackTrace is { } st) sb.AppendLine(st);
            // AggregateException: перечислить вложенные, не свёрнутые в InnerException.
            if (e is AggregateException { InnerExceptions.Count: > 1 } agg)
                foreach (var inner in agg.InnerExceptions.Skip(1))
                    sb.Append("  + ").AppendLine(FormatException(inner));
        }
        return sb.ToString();
    }

    private static string SafeFormat(string fmt, object[] args)
    {
        try { return args.Length == 0 ? fmt : string.Format(System.Globalization.CultureInfo.InvariantCulture, fmt, args); }
        catch { return fmt; }
    }

    private static void Write(LogLevel level, string category, string message)
    {
        if (level < _min) return;
        var ts = DateTime.Now;
        var line = $"{ts:yyyy-MM-dd HH:mm:ss.fff} [{level.ToString().ToUpperInvariant()}] [{category}] {message}";

        Ring.Enqueue((level.ToString().ToUpperInvariant(), category, message));
        while (Ring.Count > RingCapacity) Ring.TryDequeue(out _);

        try
        {
            lock (Gate)
            {
                if (_dir is null) return;
                var date = ts.ToString("yyyyMMdd");
                if (_writer is null || _currentDate != date)
                {
                    _writer?.Dispose();
                    _currentDate = date;
                    _writer = new StreamWriter(
                        new FileStream(Path.Combine(_dir, $"app-{date}.log"),
                            FileMode.Append, FileAccess.Write, FileShare.Read),
                        Encoding.UTF8) { AutoFlush = true };
                }
                _writer.WriteLine(line);
            }
        }
        catch { /* файл может быть занят внешним просмотрщиком — не критично */ }
    }

    /// <summary>Последние записи для отображения в GUI.</summary>
    public static IReadOnlyList<(string Level, string Category, string Message)> Recent
    {
        get
        {
            lock (Gate) return [.. Ring];
        }
    }

    public static void Flush()
    {
        lock (Gate)
        {
            try
            {
                _writer?.Flush();
                _writer?.Dispose(); // отпустить файл — чтобы его можно было читать/удалять
                _writer = null;
            }
            catch { }
        }
    }

    private static void CleanOldLogs(string dir)
    {
        foreach (var f in Directory.EnumerateFiles(dir, "app-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-RetentionDays))
                    File.Delete(f);
            }
            catch { /* занят — пропустить */ }
        }
    }
}

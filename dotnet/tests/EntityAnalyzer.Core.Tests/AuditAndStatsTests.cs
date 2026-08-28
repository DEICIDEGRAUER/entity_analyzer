using EntityAnalyzer.Core.Logging;
using Xunit;

namespace EntityAnalyzer.Core.Tests;

/// <summary>
/// R2-23 «Журнал доступа к данным»: каждая операция чтения/записи
/// пользовательских данных (%APPDATA%, корпус) оставляет audit-строку
/// в журнале — доказуемая картина обращений для институций.
/// </summary>
[Collection("LogSequential")]
public class AuditTests : IDisposable
{
    private readonly string _logDir;
    private readonly string _target;

    public AuditTests()
    {
        _logDir = Path.Combine(Path.GetTempPath(), "ea_audit_" + Guid.NewGuid().ToString("N"));
        Log.Init(_logDir, LogLevel.Debug);
        _target = Path.Combine(Path.GetTempPath(), "settings.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_logDir, recursive: true); } catch { }
    }

    private string ReadLog()
    {
        var file = Path.Combine(_logDir, $"app-{DateTime.Now:yyyyMMdd}.log");
        if (!File.Exists(file)) return "";
        // Журнал удерживается writer'ом (FileShare.Read): читатель обязан
        // явно поделиться и записью, иначе IOException.
        using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs);
        return sr.ReadToEnd();
    }

    [Fact]
    public void Write_operation_is_logged_with_kind_and_path()
    {
        Audit.Write("settings", _target);
        var log = ReadLog();
        Assert.Contains("[audit]", log);
        Assert.Contains("write settings", log);
        Assert.Contains(_target, log);
    }

    [Fact]
    public void Read_operation_is_logged()
    {
        Audit.Read("codex", _target);
        Assert.Contains("read codex", ReadLog());
    }

    [Fact]
    public void Corpus_scan_is_logged_aggregated()
    {
        Audit.CorpusScan(@"C:\corpus", 42);
        var log = ReadLog();
        Assert.Contains("scan corpus", log);
        Assert.Contains("files=42", log);
        Assert.Contains(@"C:\corpus", log);
    }
}

/// <summary>
/// R2-17 «Офлайн-статистика использования»: локальный счётчик событий
/// (stats.jsonl), никогда не покидает машину; выгрузка — вручную.
/// </summary>
[Collection("LogSequential")]
public class UsageStatsTests : IDisposable
{
    private readonly string _dir;

    public UsageStatsTests() => _dir = Path.Combine(Path.GetTempPath(), "ea_stats_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Append_creates_jsonl_with_timestamp_and_event()
    {
        UsageStats.Append(_dir, "page", "results");
        var line = File.ReadAllText(Path.Combine(_dir, "stats.jsonl"));
        Assert.Contains("\"ev\":\"page\"", line);
        Assert.Contains("\"detail\":\"results\"", line);
        Assert.Contains("\"ts\":", line);
    }

    [Fact]
    public void Multiple_events_are_appended_in_order()
    {
        UsageStats.Append(_dir, "page", "overview");
        UsageStats.Append(_dir, "command", "run-analysis");
        var lines = File.ReadAllLines(Path.Combine(_dir, "stats.jsonl"));
        Assert.Equal(2, lines.Length);
        Assert.Contains("overview", lines[0]);
        Assert.Contains("run-analysis", lines[1]);
    }

    [Fact]
    public void Count_returns_per_event_totals()
    {
        UsageStats.Append(_dir, "page", "codex");
        UsageStats.Append(_dir, "page", "codex");
        UsageStats.Append(_dir, "export", "obsidian");
        var counts = UsageStats.Count(_dir);
        Assert.Equal(2, counts["page|codex"]);
        Assert.Equal(1, counts["export|obsidian"]);
    }

    [Fact]
    public void Missing_file_gives_empty_counts()
    {
        Assert.Empty(UsageStats.Count(_dir));
    }

    [Fact]
    public void Append_never_throws_on_bad_dir()
    {
        // Контракт телеметрии: сбой записи не должен ломать приложение.
        Directory.CreateDirectory(_dir);
        var bad = Path.Combine(_dir, "file.txt"); // файл вместо каталога
        File.WriteAllText(bad, "x");
        UsageStats.Append(bad, "page", "x"); // не бросает
    }
}

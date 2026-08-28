namespace EntityAnalyzer.Core.Analysis;

/// <summary>
/// Наблюдатель корпуса (R2-6): следит за каталогами и сообщает об изменениях
/// ПОСЛЕ окна тишины — бурст сохранений редактора даёт один перезапуск анализа,
/// а не десяток. Только текстовые расширения, только существующие каталоги.
/// Потокобезопасность: событие Changed приходит из таймерного потока —
/// потребитель обязан сам маршалинг в UI-поток.
/// </summary>
public sealed class CorpusWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly System.Timers.Timer _ticker;
    private readonly Debouncer _debouncer;
    private readonly HashSet<string> _extensions;
    private bool _disposed;

    /// <summary>Корпус изменился (не чаще одного раза за окно тишины).</summary>
    public event Action? Changed;

    public CorpusWatcher(IEnumerable<string> directories, IEnumerable<string> extensions,
        TimeSpan? quietPeriod = null)
    {
        _extensions = new HashSet<string>(
            extensions.Select(e => e.StartsWith('.') ? e : "." + e),
            StringComparer.OrdinalIgnoreCase);
        _debouncer = new Debouncer(quietPeriod ?? TimeSpan.FromSeconds(2));

        foreach (var dir in directories.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var fsw = new FileSystemWatcher(dir)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            };
            fsw.Created += OnFsEvent;
            fsw.Changed += OnFsEvent;
            fsw.Renamed += OnFsEvent;
            fsw.Deleted += OnFsEvent;
            fsw.EnableRaisingEvents = true;
            _watchers.Add(fsw);
        }

        // Такт 500 мс: проверка окна тишины. Достаточно точно для секундных окон
        // и достаточно редко, чтобы не греть процессор.
        _ticker = new System.Timers.Timer(500) { AutoReset = true };
        _ticker.Elapsed += (_, _) =>
        {
            if (_debouncer.TryFire(DateTimeOffset.Now))
                Changed?.Invoke();
        };
        _ticker.Start();
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e)
    {
        // Фильтр по расширению: временные файлы офиса/браузера не будят анализ.
        if (_extensions.Count > 0 && !_extensions.Contains(Path.GetExtension(e.Name ?? "")))
            return;
        _debouncer.Poke(DateTimeOffset.Now);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ticker.Stop();
        _ticker.Dispose();
        foreach (var fsw in _watchers)
        {
            fsw.EnableRaisingEvents = false;
            fsw.Dispose();
        }
        _watchers.Clear();
    }
}

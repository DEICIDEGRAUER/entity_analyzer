using EntityAnalyzer.Core.Analysis;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

// ═══════════════ R2-6: Watch-режим корпуса ═══════════════
// Debouncer — чистая логика «окна тишины»: серия событий активности
// перезапускает таймер; срабатывание ровно одно, когда тихий период истёк.
// Такты подаются снаружи — детерминированные тесты без реального времени.
// CorpusWatcherTests — интеграция FileSystemWatcher + Debouncer на живом ФС.

public class DebouncerTests
{
    private static DateTimeOffset At(int sec, int ms = 0) =>
        new DateTimeOffset(2026, 8, 25, 12, 0, sec, TimeSpan.Zero).AddMilliseconds(ms);

    [Fact]
    public void Fires_once_after_quiet_period()
    {
        var deb = new Debouncer(TimeSpan.FromSeconds(2));
        deb.Poke(At(0));

        Assert.False(deb.TryFire(At(1)));   // тишина 1 c < 2 c
        Assert.True(deb.TryFire(At(2, 1))); // окно истекло — срабатывание
        Assert.False(deb.TryFire(At(3)));   // повторный такт без новой активности — тишина
    }

    [Fact]
    public void Burst_of_events_postpones_firing()
    {
        var deb = new Debouncer(TimeSpan.FromSeconds(2));

        // Бурст: события каждые 500 мс в течение 3 c — ни одного срабатывания.
        for (var i = 0; i <= 6; i++)
        {
            deb.Poke(At(0, i * 500));
            Assert.False(deb.TryFire(At(0, i * 500 + 100)));
        }

        // Тишина 2 c после последнего события (3.000 c) — единственное срабатывание.
        Assert.True(deb.TryFire(At(5, 1)));
    }

    [Fact]
    public void Repeated_poke_fire_cycles_work()
    {
        var deb = new Debouncer(TimeSpan.FromSeconds(1));

        deb.Poke(At(0));
        Assert.True(deb.TryFire(At(1, 1)));

        deb.Poke(At(5)); // новая активность — новый цикл
        Assert.False(deb.TryFire(At(5, 500)));
        Assert.True(deb.TryFire(At(6, 1)));
    }

    [Fact]
    public void Fire_without_poke_never_happens()
    {
        var deb = new Debouncer(TimeSpan.FromMilliseconds(100));
        for (var s = 0; s < 10; s++)
            Assert.False(deb.TryFire(At(s)));
    }
}

/// <summary>Интеграция: живой FileSystemWatcher → окно тишины → событие.</summary>
public class CorpusWatcherIntegrationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "ea_watch_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public async Task File_change_raises_debounced_event()
    {
        Directory.CreateDirectory(_dir);
        using var watcher = new CorpusWatcher([_dir], [".txt"], quietPeriod: TimeSpan.FromMilliseconds(400));

        var fired = new TaskCompletionSource();
        watcher.Changed += () => fired.TrySetResult();

        await Task.Delay(150); // даём FSW инициализироваться
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "первая правка");
        await Task.Delay(100);
        File.WriteAllText(Path.Combine(_dir, "a.txt"), "вторая правка"); // бурст

        var winner = await Task.WhenAny(fired.Task, Task.Delay(5000));
        Assert.Same(fired.Task, winner); // событие пришло до таймаута
    }

    [Fact]
    public void Dispose_stops_events()
    {
        Directory.CreateDirectory(_dir);
        var watcher = new CorpusWatcher([_dir], [".txt"], quietPeriod: TimeSpan.FromMilliseconds(200));
        watcher.Dispose();
        watcher.Dispose(); // повторный Dispose безопасен
    }
}

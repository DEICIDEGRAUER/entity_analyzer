namespace EntityAnalyzer.Core.Analysis;

/// <summary>
/// Окно тишины (R2-6): серия событий активности перезапускает отсчёт;
/// срабатывание происходит ровно один раз, когда тихий период истёк.
/// Такты подаются снаружи (TryFire) — логика детерминированна и тестируема
/// без реального времени; CorpusWatcher подаёт такты системным таймером.
/// </summary>
public sealed class Debouncer
{
    private readonly TimeSpan _quietPeriod;
    private DateTimeOffset _lastPoke;
    private bool _pending;

    public Debouncer(TimeSpan quietPeriod) => _quietPeriod = quietPeriod;

    /// <summary>Отметить активность (событие файловой системы).</summary>
    public void Poke(DateTimeOffset at)
    {
        _lastPoke = at;
        _pending = true;
    }

    /// <summary>
    /// Очередной такт. Возвращает true ровно один раз за период активности —
    /// когда с последнего события прошло ≥ тихого периода.
    /// </summary>
    public bool TryFire(DateTimeOffset now)
    {
        if (!_pending) return false;
        if (now - _lastPoke < _quietPeriod) return false;
        _pending = false;
        return true;
    }
}

namespace EntityAnalyzer.Core.Events;

/// <summary>Событие завершения анализа (R2-14). Публикуется движком в конце прогона.</summary>
public sealed record AnalysisCompletedEvent(
    DateTimeOffset CompletedAt,
    int TotalMentions,
    int FilesScanned,
    double DurationSeconds);

/// <summary>
/// Внутренняя событийная шина (R2-14): легковесный pub/sub вместо ручной
/// развязки MainViewModel → дочерние VM. Движок публикует события жизненного
/// цикла; журнал, плитки, история и будущий watch-режим подписываются.
/// Потокобезопасна; исключения подписчиков изолируются и попадают в лог —
/// сбой одного потребителя не должен ломать ни публикацию, ни остальных.
/// </summary>
public sealed class EventBus
{
    /// <summary>Шина приложения.</summary>
    public static EventBus Default { get; } = new();

    private readonly object _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private sealed record Subscription(Type Type, Delegate Handler);

    /// <summary>Подписаться на события типа T. Верните IDisposable для отписки.</summary>
    public IDisposable Subscribe<T>(Action<T> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate) _subscriptions.Add(new Subscription(typeof(T), handler));
        return new Unsubscription(this, typeof(T), handler);
    }

    /// <summary>Опубликовать событие всем подписчикам этого типа.</summary>
    public void Publish<T>(T evt)
    {
        Delegate[] snapshot;
        lock (_gate)
            snapshot = [.. _subscriptions.Where(s => s.Type == typeof(T)).Select(s => s.Handler)];

        foreach (var handler in snapshot.OfType<Action<T>>())
        {
            try { handler(evt); }
            catch (Exception ex)
            {
                Logging.Log.Error("events", $"Подписчик {typeof(T).Name} упал: {ex.Message}");
            }
        }
    }

    private sealed class Unsubscription(EventBus bus, Type type, Delegate handler) : IDisposable
    {
        public void Dispose()
        {
            lock (bus._gate)
                bus._subscriptions.RemoveAll(s => s.Type == type && ReferenceEquals(s.Handler, handler));
        }
    }
}

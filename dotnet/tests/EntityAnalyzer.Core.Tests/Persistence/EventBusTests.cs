using EntityAnalyzer.Core.Events;
using Xunit;

namespace EntityAnalyzer.Core.Tests.Persistence;

// ═══════════════ R2-14: Внутренняя событийная шина ═══════════════
// Легковесный pub/sub: движок публикует события жизненного цикла прогона,
// журнал/плитки/история/watch подписываются вместо ручной развязки VM.
// Контракт: доставка по типу, отписка, изоляция исключений подписчиков,
// потокобезопасность.

public class EventBusTests
{
    public sealed record Ping(int N);
    public sealed record Pong(int N);

    [Fact]
    public void Delivers_to_matching_subscriber()
    {
        var bus = new EventBus();
        var received = new List<int>();
        using var _ = bus.Subscribe<Ping>(p => received.Add(p.N));

        bus.Publish(new Ping(42));

        Assert.Equal([42], received);
    }

    [Fact]
    public void Other_event_types_are_not_delivered()
    {
        var bus = new EventBus();
        var received = new List<int>();
        using var _ = bus.Subscribe<Ping>(p => received.Add(p.N));

        bus.Publish(new Pong(7));

        Assert.Empty(received);
    }

    [Fact]
    public void Unsubscribe_stops_delivery()
    {
        var bus = new EventBus();
        var received = new List<int>();
        var sub = bus.Subscribe<Ping>(p => received.Add(p.N));

        bus.Publish(new Ping(1));
        sub.Dispose();
        bus.Publish(new Ping(2));

        Assert.Equal([1], received);
    }

    [Fact]
    public void Subscriber_exception_is_isolated()
    {
        var bus = new EventBus();
        var received = new List<int>();
        using var bad = bus.Subscribe<Ping>(_ => throw new InvalidOperationException("подписчик сломан"));
        using var good = bus.Subscribe<Ping>(p => received.Add(p.N));

        // Публикация не бросает, второй подписчик получает событие.
        bus.Publish(new Ping(5));

        Assert.Equal([5], received);
    }

    [Fact]
    public void All_subscribers_receive_event()
    {
        var bus = new EventBus();
        var a = new List<int>();
        var b = new List<int>();
        using var _1 = bus.Subscribe<Ping>(p => a.Add(p.N));
        using var _2 = bus.Subscribe<Ping>(p => b.Add(p.N));

        bus.Publish(new Ping(9));

        Assert.Equal([9], a);
        Assert.Equal([9], b);
    }

    [Fact]
    public async Task Subscribe_during_publish_does_not_corrupt_state()
    {
        var bus = new EventBus();
        using var _ = bus.Subscribe<Ping>(_ => { });
        var tasks = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                for (var i = 0; i < 200; i++)
                {
                    using var s = bus.Subscribe<Ping>(_ => { });
                    bus.Publish(new Ping(i));
                }
            }))
            .ToArray();
        await Task.WhenAll(tasks);
        // Контракт — отсутствие исключений и порчи состояния; счётчики не проверяем.
    }
}

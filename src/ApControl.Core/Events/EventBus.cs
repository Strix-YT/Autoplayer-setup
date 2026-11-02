using System.Threading.Channels;

namespace ApControl.Core.Events;

/// <summary>
/// Something the dashboard should know about. Kind is one of:
/// "client" (Data = ClientSnapshot), "command" (Data = BulkResult),
/// "workflow" (Data = WorkflowRunSnapshot), "log".
/// </summary>
public sealed record ControlEvent(DateTimeOffset At, string Kind, string? ClientId, string Message, object? Data = null);

/// <summary>In-process pub/sub with a ring buffer of recent events for late subscribers.</summary>
public sealed class EventBus
{
    private readonly object _gate = new();
    private readonly List<Channel<ControlEvent>> _subscribers = new();
    private readonly Queue<ControlEvent> _recent = new();
    private readonly int _recentCapacity;

    public EventBus(int recentCapacity = 500) => _recentCapacity = recentCapacity;

    public void Publish(string kind, string? clientId, string message, object? data = null) =>
        Publish(new ControlEvent(DateTimeOffset.UtcNow, kind, clientId, message, data));

    public void Publish(ControlEvent evt)
    {
        lock (_gate)
        {
            _recent.Enqueue(evt);
            while (_recent.Count > _recentCapacity) _recent.Dequeue();
            foreach (var sub in _subscribers) sub.Writer.TryWrite(evt);
        }
    }

    public IReadOnlyList<ControlEvent> Recent(int max = 200)
    {
        lock (_gate)
        {
            return _recent.Skip(Math.Max(0, _recent.Count - max)).ToList();
        }
    }

    public Subscription Subscribe()
    {
        // Bounded + drop-oldest so a slow browser tab can never stall the controller.
        var channel = Channel.CreateBounded<ControlEvent>(new BoundedChannelOptions(1000)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true
        });
        lock (_gate) _subscribers.Add(channel);
        return new Subscription(this, channel);
    }

    private void Unsubscribe(Channel<ControlEvent> channel)
    {
        lock (_gate) _subscribers.Remove(channel);
        channel.Writer.TryComplete();
    }

    public sealed class Subscription : IDisposable
    {
        private readonly EventBus _bus;
        private readonly Channel<ControlEvent> _channel;
        internal Subscription(EventBus bus, Channel<ControlEvent> channel) { _bus = bus; _channel = channel; }
        public ChannelReader<ControlEvent> Reader => _channel.Reader;
        public void Dispose() => _bus.Unsubscribe(_channel);
    }
}

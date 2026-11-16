using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ASync.Agent.Control;

/// <summary>
/// Fans an agent's events out to its subscribers: the control channel's <c>subscribe</c> connections
/// (<see cref="ControlServer"/>) and, in an app that runs the agent in its own process (Android, iOS), the app itself.
/// <see cref="Publish"/> never blocks: a subscriber that does not keep up loses its oldest events.
/// </summary>
public sealed class ControlEventHub
{
    /// <summary>Events a subscriber may fall behind by before the oldest are dropped.</summary>
    public const int Capacity = 256;

    private readonly ConcurrentDictionary<Channel<ControlEvent>, byte> _subscribers = new();

    public int SubscriberCount => _subscribers.Count;

    /// <summary>Starts receiving events; dispose the subscription to stop.</summary>
    public Subscription Subscribe()
    {
        var channel = Channel.CreateBounded<ControlEvent>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
        });
        _subscribers[channel] = 0;
        return new Subscription(this, channel);
    }

    /// <summary>Sends an event to every subscriber (never blocks).</summary>
    public void Publish(ControlEvent e)
    {
        foreach (var channel in _subscribers.Keys) channel.Writer.TryWrite(e);
    }

    /// <summary>Ends every subscription: their readers complete after the events already queued.</summary>
    public void Complete()
    {
        foreach (var channel in _subscribers.Keys) channel.Writer.TryComplete();
    }

    public sealed class Subscription : IDisposable
    {
        private readonly ControlEventHub _hub;
        private readonly Channel<ControlEvent> _channel;

        internal Subscription(ControlEventHub hub, Channel<ControlEvent> channel)
        {
            _hub = hub;
            _channel = channel;
        }

        public ChannelReader<ControlEvent> Reader => _channel.Reader;

        public void Dispose()
        {
            _hub._subscribers.TryRemove(_channel, out _);
            _channel.Writer.TryComplete();
        }
    }
}

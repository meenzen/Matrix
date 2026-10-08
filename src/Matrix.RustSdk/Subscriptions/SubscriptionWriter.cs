using System.Threading.Channels;

namespace Matrix.RustSdk.Subscriptions;

/// <summary>
/// The channel of a subscription. The generated listeners call <see cref="Write"/> on a thread of the SDK,
/// <see cref="SubscriptionStream"/> reads the values and calls <see cref="WriteCurrent"/> with the current value of
/// subscriptions that don't deliver it themselves.
/// </summary>
/// <remarks>
/// Values that can't be delivered are disposed after releasing the lock, disposing frees native objects and must not
/// run while the thread of the SDK waits for the lock.
/// </remarks>
internal sealed class SubscriptionWriter<T>
{
    private readonly Lock _lock = new();
    private readonly Channel<T> _channel;
    private bool _written;

    // the value the channel dropped while a write held the lock, only accessed under the lock
    private object? _dropped;

    public SubscriptionWriter(SubscriptionBuffer buffer)
    {
        _channel = buffer switch
        {
            SubscriptionBuffer.Latest => Channel.CreateBounded<T>(
                new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
                // called by TryWrite, which only runs under the lock
                dropped => _dropped = dropped
            ),
            _ => Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = true }),
        };
    }

    public ChannelReader<T> Reader => _channel.Reader;

    /// <summary>
    /// Writes a value of the SDK, or disposes it if the subscription ended. Never blocks for long and never throws,
    /// the SDK calls the listeners from its async runtime and exceptions must not escape into it.
    /// </summary>
    public void Write(T value)
    {
        object? obsolete;
        lock (_lock)
        {
            _written = true;
            obsolete = WriteLocked(value);
        }
        SubscriptionStream.Dispose(obsolete);
    }

    /// <summary>
    /// Writes the current value fetched after subscribing, unless the SDK already wrote a value, which is at least as
    /// new.
    /// </summary>
    public void WriteCurrent(T value)
    {
        object? obsolete;
        lock (_lock)
        {
            obsolete = _written ? value : WriteLocked(value);
        }
        SubscriptionStream.Dispose(obsolete);
    }

    /// <summary>
    /// Completes the channel, values written afterwards are disposed. Values written before can still be read.
    /// </summary>
    public void Complete() => _channel.Writer.TryComplete();

    /// <summary>
    /// Writes <paramref name="value"/> and returns the value to dispose: <paramref name="value"/> if the channel is
    /// completed, the value it dropped to make room, or <see langword="null"/>.
    /// </summary>
    private object? WriteLocked(T value)
    {
        if (!_channel.Writer.TryWrite(value))
        {
            return value;
        }
        object? dropped = _dropped;
        _dropped = null;
        return dropped;
    }
}

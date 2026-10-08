using System.Threading.Channels;

namespace Matrix.RustSdk.Subscriptions;

/// <summary>
/// Writes the values of a subscription to its channel. The generated listeners call <see cref="Write"/> on a thread of
/// the SDK, <see cref="SubscriptionStream"/> calls <see cref="WriteCurrent"/> with the current value of subscriptions
/// that don't deliver it themselves.
/// </summary>
internal sealed class SubscriptionWriter<T>(ChannelWriter<T> channel)
{
    private readonly Lock _lock = new();
    private bool _written;

    /// <summary>
    /// Writes a value of the SDK, or disposes it if the enumeration ended. Never blocks and never throws, the SDK calls
    /// the listeners from its async runtime and exceptions must not escape into it.
    /// </summary>
    public void Write(T value)
    {
        lock (_lock)
        {
            _written = true;
            if (!channel.TryWrite(value))
            {
                SubscriptionStream.Dispose(value);
            }
        }
    }

    /// <summary>
    /// Writes the current value fetched after subscribing, unless the SDK already wrote a value, which is at least as
    /// new.
    /// </summary>
    public void WriteCurrent(T value)
    {
        lock (_lock)
        {
            if (_written || !channel.TryWrite(value))
            {
                SubscriptionStream.Dispose(value);
            }
        }
    }
}

using System.Collections;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk.Subscriptions;

/// <summary>
/// Turns the listener and <see cref="TaskHandle"/> pairs of the bindings into <see cref="IAsyncEnumerable{T}"/>. Used
/// by the implementations the source generator writes for <see cref="SubscriptionAttribute"/>.
/// </summary>
internal static class SubscriptionStream
{
    /// <summary>
    /// Subscribes when the enumeration starts and cancels the subscription when it ends. Values the consumer didn't
    /// read are disposed.
    /// </summary>
    public static async IAsyncEnumerable<T> CreateAsync<T>(
        Func<ChannelWriter<T>, ValueTask<TaskHandle>> subscribe,
        SubscriptionBuffer buffer,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        Channel<T> channel = buffer switch
        {
            SubscriptionBuffer.Latest => Channel.CreateBounded<T>(
                new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
                dropped => Dispose(dropped)
            ),
            _ => Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = true }),
        };

        TaskHandle handle = await subscribe(channel.Writer).ConfigureAwait(false);
        try
        {
            while (await channel.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out T? value))
                {
                    yield return value;
                }
            }
        }
        finally
        {
            // complete first, values the SDK delivers while the task is cancelled are disposed by Write
            channel.Writer.TryComplete();
            handle.Dispose();
            while (channel.Reader.TryRead(out T? value))
            {
                Dispose(value);
            }
        }
    }

    /// <summary>
    /// Called by the generated listeners on a thread of the SDK. Never throws, exceptions must not escape into the
    /// SDK.
    /// </summary>
    public static void Write<T>(ChannelWriter<T> writer, T value)
    {
        if (!writer.TryWrite(value))
        {
            Dispose(value);
        }
    }

    private static void Dispose(object? value)
    {
        switch (value)
        {
            case IDisposable disposable:
                disposable.Dispose();
                break;
            case ITuple tuple:
                for (int i = 0; i < tuple.Length; i++)
                {
                    Dispose(tuple[i]);
                }
                break;
            case IEnumerable values and not string:
                foreach (object? item in values)
                {
                    Dispose(item);
                }
                break;
        }
    }
}

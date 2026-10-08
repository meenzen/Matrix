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
    /// How often an idle enumeration checks whether the task of the SDK finished. The SDK doesn't notify the listener
    /// when a subscription ends on its own (its owner was disposed, it lagged behind, a sync failed), without the check
    /// the enumeration would wait forever.
    /// </summary>
    internal static readonly TimeSpan FinishedCheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Subscribes when the enumeration starts and cancels the subscription when it ends. The enumeration ends when
    /// the task of the SDK finishes, values the consumer didn't read are disposed.
    /// </summary>
    /// <param name="subscribe">Subscribes with a listener writing to the given writer.</param>
    /// <param name="buffer">How values are buffered until the consumer reads them.</param>
    /// <param name="current">
    /// Fetches the current value after subscribing, for subscriptions that only deliver changes. It is yielded first
    /// unless the SDK delivered a value in the meantime.
    /// </param>
    /// <param name="throwWhenFinished">
    /// Throw an <see cref="InvalidOperationException"/> instead of ending the enumeration when the task of the SDK
    /// finishes, for subscriptions that only end because of an error.
    /// </param>
    /// <param name="finishedCheckInterval">How often an idle enumeration checks whether the task finished.</param>
    /// <param name="cancellationToken">Cancels the enumeration.</param>
    public static async IAsyncEnumerable<T> CreateAsync<T, THandle>(
        Func<SubscriptionWriter<T>, ValueTask<THandle>> subscribe,
        SubscriptionBuffer buffer,
        Func<ValueTask<T>>? current = null,
        bool throwWhenFinished = false,
        TimeSpan? finishedCheckInterval = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
        where THandle : ITaskHandle, IDisposable
    {
        Channel<T> channel = buffer switch
        {
            SubscriptionBuffer.Latest => Channel.CreateBounded<T>(
                new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
                dropped => Dispose(dropped)
            ),
            _ => Channel.CreateUnbounded<T>(new UnboundedChannelOptions { SingleReader = true }),
        };
        // the SDK calls some listeners with the current value before the subscription method returns
        SubscriptionWriter<T> writer = new(channel.Writer);
        TimeSpan interval = finishedCheckInterval ?? FinishedCheckInterval;

        THandle handle = await subscribe(writer).ConfigureAwait(false);
        try
        {
            if (current is not null)
            {
                writer.WriteCurrent(await current().ConfigureAwait(false));
            }

            while (await WaitToReadAsync(channel, handle, interval, cancellationToken).ConfigureAwait(false))
            {
                while (channel.Reader.TryRead(out T? value))
                {
                    yield return value;
                }
            }

            if (throwWhenFinished)
            {
                throw new InvalidOperationException(
                    "The SDK ended the subscription because of an error, the SDK logs contain the details."
                );
            }
        }
        finally
        {
            // complete first, values the SDK delivers while the task is cancelled are disposed by the writer
            channel.Writer.TryComplete();
            handle.Dispose();
            while (channel.Reader.TryRead(out T? value))
            {
                Dispose(value);
            }
        }
    }

    /// <summary>
    /// Disposes <paramref name="value"/> if it is <see cref="IDisposable"/>, and the elements of arrays and tuples.
    /// </summary>
    internal static void Dispose(object? value)
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

    /// <summary>
    /// Waits until a value can be read, false once the channel is completed. Checks every
    /// <paramref name="finishedCheckInterval"/> whether the task finished and completes the channel if it did, values
    /// written before can still be read.
    /// </summary>
    private static async ValueTask<bool> WaitToReadAsync<T>(
        Channel<T> channel,
        ITaskHandle handle,
        TimeSpan finishedCheckInterval,
        CancellationToken cancellationToken
    )
    {
        ValueTask<bool> wait = channel.Reader.WaitToReadAsync(cancellationToken);
        if (wait.IsCompleted)
        {
            return await wait.ConfigureAwait(false);
        }

        // a single pending wait, channels with a single reader don't support several
        Task<bool> waiting = wait.AsTask();
        while (true)
        {
            Task completed = await Task.WhenAny(waiting, Task.Delay(finishedCheckInterval, cancellationToken))
                .ConfigureAwait(false);
            if (completed == waiting)
            {
                return await waiting.ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (handle.IsFinished())
            {
                channel.Writer.TryComplete();
                return await waiting.ConfigureAwait(false);
            }
        }
    }
}

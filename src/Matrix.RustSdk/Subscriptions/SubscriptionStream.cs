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
    /// <param name="cancellationToken">Cancels the enumeration, checked before subscribing and every value.</param>
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
        // subscribing has side effects (a sync v2 loop sends a request), don't subscribe when cancelled already
        cancellationToken.ThrowIfCancellationRequested();

        // the SDK calls some listeners with the current value before the subscription method returns
        SubscriptionWriter<T> writer = new(buffer);
        TimeSpan interval = finishedCheckInterval ?? FinishedCheckInterval;

        THandle handle;
        try
        {
            handle = await subscribe(writer).ConfigureAwait(false);
        }
        catch
        {
            // values the listener received before subscribing failed
            Drain(writer);
            throw;
        }

        try
        {
            if (current is not null)
            {
                writer.WriteCurrent(await current().ConfigureAwait(false));
            }

            while (await WaitToReadAsync(writer, handle, interval, cancellationToken).ConfigureAwait(false))
            {
                while (writer.Reader.TryRead(out T? value))
                {
                    // a slow consumer can have many values buffered, cancellation must not wait for all of them
                    if (cancellationToken.IsCancellationRequested)
                    {
                        Dispose(value);
                        cancellationToken.ThrowIfCancellationRequested();
                    }
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
            writer.Complete();
            handle.Dispose();
            Drain(writer);
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
    /// Completes the channel and disposes the values nobody will read.
    /// </summary>
    private static void Drain<T>(SubscriptionWriter<T> writer)
    {
        writer.Complete();
        while (writer.Reader.TryRead(out T? value))
        {
            Dispose(value);
        }
    }

    /// <summary>
    /// Waits until a value can be read, false once the channel is completed. Checks every
    /// <paramref name="finishedCheckInterval"/> whether the task finished and completes the channel if it did, values
    /// written before can still be read.
    /// </summary>
    private static async ValueTask<bool> WaitToReadAsync<T>(
        SubscriptionWriter<T> writer,
        ITaskHandle handle,
        TimeSpan finishedCheckInterval,
        CancellationToken cancellationToken
    )
    {
        ValueTask<bool> wait = writer.Reader.WaitToReadAsync(cancellationToken);
        if (wait.IsCompleted)
        {
            return await wait.ConfigureAwait(false);
        }

        // a single pending wait, channels with a single reader don't support several. The delays are cancelled when
        // the wait ends, otherwise every wait would leave a timer behind.
        Task<bool> waiting = wait.AsTask();
        using CancellationTokenSource delays = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            while (true)
            {
                Task completed = await Task.WhenAny(waiting, Task.Delay(finishedCheckInterval, delays.Token))
                    .ConfigureAwait(false);
                if (completed == waiting)
                {
                    return await waiting.ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (handle.IsFinished())
                {
                    writer.Complete();
                    return await waiting.ConfigureAwait(false);
                }
            }
        }
        finally
        {
            await delays.CancelAsync().ConfigureAwait(false);
        }
    }
}

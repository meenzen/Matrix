using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Subscriptions;
using TUnit.Assertions.Enums;

namespace Matrix.RustSdk.Tests;

/// <summary>
/// The runtime part of the generated subscriptions with a fake <see cref="TaskHandle"/>, no native library needed.
/// </summary>
public class SubscriptionStreamTests
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(20);

    [Test]
    public async Task Subscribe_ShouldNotBeCalledBeforeEnumerationStarts()
    {
        // Arrange
        int subscriptions = 0;

        // Act
        IAsyncEnumerable<int> values = Create<int>(
            SubscriptionBuffer.All,
            (_, handle) =>
            {
                subscriptions++;
                handle.Finish();
            },
            out _
        );
        int beforeEnumeration = subscriptions;
        await foreach (int _ in values) { }

        // Assert
        await Assert.That(beforeEnumeration).IsEqualTo(0);
        await Assert.That(subscriptions).IsEqualTo(1);
    }

    [Test]
    public async Task BufferAll_ShouldYieldEveryValueInOrder()
    {
        // Arrange
        IAsyncEnumerable<int> values = Create<int>(
            SubscriptionBuffer.All,
            (writer, handle) =>
            {
                // like the SDK, which calls some listeners before the subscription method returns
                foreach (int value in Enumerable.Range(1, 5))
                {
                    writer.Write(value);
                }
                handle.Finish();
            },
            out _
        );

        // Act
        List<int> received = await ToListAsync(values);

        // Assert
        await Assert.That(received).IsEquivalentTo([1, 2, 3, 4, 5], CollectionOrdering.Matching);
    }

    [Test]
    public async Task BufferLatest_ShouldKeepOnlyTheLatestUnreadValueAndDisposeTheOthers()
    {
        // Arrange
        Value[] written = [new(1), new(2), new(3)];
        IAsyncEnumerable<Value> values = Create<Value>(
            SubscriptionBuffer.Latest,
            (writer, handle) =>
            {
                foreach (Value value in written)
                {
                    writer.Write(value);
                }
                handle.Finish();
            },
            out _
        );

        // Act
        List<Value> received = await ToListAsync(values);

        // Assert
        await Assert.That(received.Select(value => value.Number)).IsEquivalentTo([3]);
        await Assert.That(written[0].IsDisposed).IsTrue();
        await Assert.That(written[1].IsDisposed).IsTrue();
        await Assert.That(written[2].IsDisposed).IsFalse();
    }

    [Test]
    public async Task Break_ShouldDisposeTheHandleAndTheElementsOfUnreadValues()
    {
        // Arrange
        Value[] written = [new(1), new(2)];
        IAsyncEnumerable<Value[]> values = Create<Value[]>(
            SubscriptionBuffer.All,
            (writer, _) =>
            {
                writer.Write([written[0]]);
                writer.Write([written[1]]);
            },
            out Func<FakeHandle?> handle
        );

        // Act
        await foreach (Value[] _ in values)
        {
            break;
        }

        // Assert
        await Assert.That(handle()!.IsDisposed).IsTrue();
        await Assert.That(written[0].IsDisposed).IsFalse();
        await Assert.That(written[1].IsDisposed).IsTrue();
    }

    [Test]
    public async Task CancellationWhileWaiting_ShouldThrowOperationCanceledException()
    {
        // Arrange
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(100));
        IAsyncEnumerable<int> values = Create<int>(SubscriptionBuffer.All, (_, _) => { }, out Func<FakeHandle?> handle);

        // Act
        async Task EnumerateAsync()
        {
            await foreach (int _ in values.WithCancellation(cancellation.Token)) { }
        }

        // Assert
        await Assert.That(EnumerateAsync).Throws<OperationCanceledException>();
        await Assert.That(handle()!.IsDisposed).IsTrue();
    }

    [Test]
    public async Task FinishedTask_ShouldEndTheEnumerationAfterTheRemainingValues()
    {
        // Arrange
        SubscriptionWriter<int>? listener = null;
        IAsyncEnumerable<int> values = Create<int>(
            SubscriptionBuffer.All,
            (writer, _) =>
            {
                listener = writer;
                writer.Write(0);
            },
            out Func<FakeHandle?> handle
        );
        List<int> received = [];

        // Act
        await foreach (int value in values)
        {
            received.Add(value);
            if (value == 1)
            {
                // the SDK writes a last value and its task ends without telling the listener
                listener!.Write(2);
                handle()!.Finish();
            }
            else if (value == 0)
            {
                listener!.Write(1);
            }
        }

        // Assert
        await Assert.That(received).IsEquivalentTo([0, 1, 2], CollectionOrdering.Matching);
        await Assert.That(handle()!.IsDisposed).IsTrue();
    }

    [Test]
    public async Task ThrowWhenFinished_ShouldThrowAfterTheRemainingValues()
    {
        // Arrange
        IAsyncEnumerable<int> values = Create<int>(
            SubscriptionBuffer.All,
            (writer, handle) =>
            {
                writer.Write(1);
                handle.Finish();
            },
            out Func<FakeHandle?> handle,
            throwWhenFinished: true
        );
        List<int> received = [];

        // Act
        async Task EnumerateAsync()
        {
            await foreach (int value in values)
            {
                received.Add(value);
            }
        }

        // Assert
        await Assert.That(EnumerateAsync).Throws<InvalidOperationException>();
        await Assert.That(received).IsEquivalentTo([1]);
        await Assert.That(handle()!.IsDisposed).IsTrue();
    }

    [Test]
    public async Task ValuesWrittenAfterTheEnd_ShouldBeDisposed()
    {
        // Arrange
        SubscriptionWriter<Value>? listener = null;
        IAsyncEnumerable<Value> values = Create<Value>(
            SubscriptionBuffer.All,
            (writer, handle) =>
            {
                listener = writer;
                handle.Finish();
            },
            out _
        );
        await foreach (Value _ in values) { }
        Value late = new(1);

        // Act
        listener!.Write(late);

        // Assert
        await Assert.That(late.IsDisposed).IsTrue();
    }

    [Test]
    public async Task Current_ShouldBeYieldedFirst()
    {
        // Arrange
        SubscriptionWriter<int>? listener = null;
        IAsyncEnumerable<int> values = Create<int>(
            SubscriptionBuffer.Latest,
            (writer, _) => listener = writer,
            out Func<FakeHandle?> handle,
            current: () => ValueTask.FromResult(1)
        );
        List<int> received = [];

        // Act
        await foreach (int value in values)
        {
            received.Add(value);
            if (value == 1)
            {
                listener!.Write(2);
                handle()!.Finish();
            }
        }

        // Assert
        await Assert.That(received).IsEquivalentTo([1, 2], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Current_ShouldBeSkippedAndDisposedWhenTheSdkWasFaster()
    {
        // Arrange
        Value fromSdk = new(2);
        Value current = new(1);
        IAsyncEnumerable<Value> values = Create<Value>(
            SubscriptionBuffer.Latest,
            (writer, handle) =>
            {
                // the SDK delivers a value before the current one was fetched, it is at least as new
                writer.Write(fromSdk);
                handle.Finish();
            },
            out _,
            current: () => ValueTask.FromResult(current)
        );

        // Act
        List<Value> received = await ToListAsync(values);

        // Assert
        await Assert.That(received.Select(value => value.Number)).IsEquivalentTo([2]);
        await Assert.That(current.IsDisposed).IsTrue();
        await Assert.That(fromSdk.IsDisposed).IsFalse();
    }

    [Test]
    public async Task FailingCurrent_ShouldThrowAndDisposeTheHandle()
    {
        // Arrange
        IAsyncEnumerable<int> values = Create<int>(
            SubscriptionBuffer.Latest,
            (_, _) => { },
            out Func<FakeHandle?> handle,
            current: () => throw new InvalidOperationException("current failed")
        );

        // Act
        async Task EnumerateAsync()
        {
            await foreach (int _ in values) { }
        }

        // Assert
        await Assert.That(EnumerateAsync).Throws<InvalidOperationException>().WithMessage("current failed");
        await Assert.That(handle()!.IsDisposed).IsTrue();
    }

    [Test]
    public async Task CurrentReplacedByANewerValue_ShouldBeDisposed()
    {
        // Arrange
        SubscriptionWriter<Value>? listener = null;
        FakeHandle? subscription = null;
        Value current = new(1);
        Value newer = new(2);
        IAsyncEnumerable<Value> values = Create<Value>(
            SubscriptionBuffer.Latest,
            (writer, handle) =>
            {
                listener = writer;
                subscription = handle;
            },
            out _,
            current: () =>
            {
                // the SDK delivers a newer value after the current one is written, before the consumer reads it
                _ = Task.Run(async () =>
                {
                    await Task.Delay(10);
                    listener!.Write(newer);
                    subscription!.Finish();
                });
                return ValueTask.FromResult(current);
            }
        );

        // Act
        List<Value> received = [];
        await foreach (Value value in values)
        {
            received.Add(value);
        }

        // Assert
        await Assert.That(received).Contains(newer);
        await Assert.That(current.IsDisposed).IsEqualTo(!received.Contains(current));
        await Assert.That(newer.IsDisposed).IsFalse();
    }

    [Test]
    public async Task FailingSubscribe_ShouldDisposeTheValuesWrittenBefore()
    {
        // Arrange
        Value written = new(1);
        IAsyncEnumerable<Value> values = SubscriptionStream.CreateAsync<Value, FakeHandle>(
            writer =>
            {
                // the SDK called the listener synchronously, then subscribing failed
                writer.Write(written);
                throw new InvalidOperationException("subscribing failed");
            },
            SubscriptionBuffer.All,
            finishedCheckInterval: CheckInterval
        );

        // Act
        async Task EnumerateAsync()
        {
            await foreach (Value _ in values) { }
        }

        // Assert
        await Assert.That(EnumerateAsync).Throws<InvalidOperationException>().WithMessage("subscribing failed");
        await Assert.That(written.IsDisposed).IsTrue();
    }

    [Test]
    public async Task UnreadTuples_ShouldDisposeTheirElements()
    {
        // Arrange
        Value first = new(1);
        Value second = new(2);
        IAsyncEnumerable<(string RoomId, Value Update)> values = Create<(string, Value)>(
            SubscriptionBuffer.All,
            (writer, _) =>
            {
                writer.Write(("!a:localhost", first));
                writer.Write(("!b:localhost", second));
            },
            out _
        );

        // Act
        await foreach ((string RoomId, Value Update) _ in values)
        {
            break;
        }

        // Assert
        await Assert.That(first.IsDisposed).IsFalse();
        await Assert.That(second.IsDisposed).IsTrue();
    }

    [Test]
    public async Task CancelledTokenParameter_ShouldEndTheEnumerationBeforeBufferedValues()
    {
        // Arrange
        using CancellationTokenSource cancellation = new();
        Value[] written = [new(1), new(2), new(3)];
        IAsyncEnumerable<Value> values = Create<Value>(
            SubscriptionBuffer.All,
            (writer, _) =>
            {
                foreach (Value value in written)
                {
                    writer.Write(value);
                }
            },
            out Func<FakeHandle?> handle,
            cancellationToken: cancellation.Token
        );
        List<Value> received = [];

        // Act
        async Task EnumerateAsync()
        {
            await foreach (Value value in values)
            {
                received.Add(value);
                await cancellation.CancelAsync();
            }
        }

        // Assert
        await Assert.That(EnumerateAsync).Throws<OperationCanceledException>();
        await Assert.That(received.Select(value => value.Number)).IsEquivalentTo([1]);
        await Assert.That(written[1].IsDisposed).IsTrue();
        await Assert.That(written[2].IsDisposed).IsTrue();
        await Assert.That(handle()!.IsDisposed).IsTrue();
    }

    [Test]
    public async Task CancelledToken_ShouldNotSubscribe()
    {
        // Arrange
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();
        int subscriptions = 0;
        IAsyncEnumerable<int> values = Create<int>(
            SubscriptionBuffer.All,
            (_, _) => subscriptions++,
            out _,
            cancellationToken: cancellation.Token
        );

        // Act
        async Task EnumerateAsync()
        {
            await foreach (int _ in values) { }
        }

        // Assert
        await Assert.That(EnumerateAsync).Throws<OperationCanceledException>();
        await Assert.That(subscriptions).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ConcurrentWriter_ShouldYieldOrDisposeEveryValueOnce(bool latest)
    {
        for (int iteration = 0; iteration < 50; iteration++)
        {
            // Arrange
            List<Value> written = [];
            Task? sdk = null;
            IAsyncEnumerable<Value> values = Create<Value>(
                latest ? SubscriptionBuffer.Latest : SubscriptionBuffer.All,
                (writer, handle) =>
                    sdk = Task.Run(() =>
                    {
                        // like the SDK, the listener keeps being called while the subscription ends
                        for (int i = 0; i < 200; i++)
                        {
                            Value value = new(i);
                            lock (written)
                            {
                                written.Add(value);
                            }
                            writer.Write(value);
                        }
                        handle.Finish();
                    }),
                out _
            );
            List<Value> received = [];

            // Act
            await foreach (Value value in values)
            {
                received.Add(value);
                if (received.Count == 10)
                {
                    break;
                }
            }
            await sdk!;

            // Assert
            Value[] lost = [.. written.Where(value => !value.IsDisposed && !received.Contains(value))];
            Value[] disposedTwice = [.. written.Where(value => value.DisposeCount > 1)];
            await Assert.That(lost).IsEmpty();
            await Assert.That(disposedTwice).IsEmpty();
            await Assert.That(received.Where(value => value.IsDisposed)).IsEmpty();
        }
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> values)
    {
        List<T> list = [];
        await foreach (T value in values)
        {
            list.Add(value);
        }
        return list;
    }

    /// <summary>
    /// A subscription that calls <paramref name="onSubscribe"/> with the writer of the listener and the handle.
    /// <paramref name="handle"/> returns the handle of the latest subscription.
    /// </summary>
    private static IAsyncEnumerable<T> Create<T>(
        SubscriptionBuffer buffer,
        Action<SubscriptionWriter<T>, FakeHandle> onSubscribe,
        out Func<FakeHandle?> handle,
        Func<ValueTask<T>>? current = null,
        bool throwWhenFinished = false,
        CancellationToken cancellationToken = default
    )
    {
        FakeHandle? latest = null;
        handle = () => latest;
        return SubscriptionStream.CreateAsync<T, FakeHandle>(
            writer =>
            {
                latest = new FakeHandle();
                onSubscribe(writer, latest);
                return ValueTask.FromResult(latest);
            },
            buffer,
            current,
            throwWhenFinished,
            CheckInterval,
            cancellationToken
        );
    }

    private sealed class FakeHandle : ITaskHandle, IDisposable
    {
        private volatile bool _finished;

        public bool IsDisposed { get; private set; }

        public void Finish() => _finished = true;

        public void Cancel() => _finished = true;

        public bool IsFinished() => _finished;

        public void Dispose()
        {
            IsDisposed = true;
            Cancel();
        }
    }

    private sealed class Value(int number) : IDisposable
    {
        public int Number { get; } = number;

        private int _disposeCount;

        public int DisposeCount => _disposeCount;

        public bool IsDisposed => _disposeCount > 0;

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}

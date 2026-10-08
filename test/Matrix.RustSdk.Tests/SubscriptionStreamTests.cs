using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Subscriptions;

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
        await Assert.That(received).IsEquivalentTo([1, 2, 3, 4, 5]);
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
        await Assert.That(received).IsEquivalentTo([0, 1, 2]);
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
        await Assert.That(received).IsEquivalentTo([1, 2]);
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
        bool throwWhenFinished = false
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
            CheckInterval
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

        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}

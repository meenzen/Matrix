using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Matrix.RustSdk.Testing;
using TUnit.Assertions.Enums;

namespace Matrix.RustSdk.Tests;

/// <summary>
/// <see cref="LiveList{T}"/> with diffs written to a channel, without the SDK.
/// </summary>
public class LiveListTests
{
    // updates the lists on thread pool threads, the tests don't depend on the context of the test runner
    private static readonly SynchronizationContext ThreadPool = new();

    [Test]
    public async Task Diffs_ShouldRaiseSingleItemEventsMatchingTheList()
    {
        // Arrange
        DiffSource<int> source = new();
        await using LiveList<int> list = source.Diffs.ToLiveList(ThreadPool);
        List<string> events = [];
        list.CollectionChanged += (_, e) => events.Add($"{Describe(e)} -> {string.Join(",", list)}");

        // Act
        await source.SendAsync(list, new VectorDiff<int>.Reset([1, 2, 3]));
        await source.SendAsync(
            list,
            new VectorDiff<int>.Append([4, 5]),
            new VectorDiff<int>.PushFront(0),
            new VectorDiff<int>.Insert(2, 9),
            new VectorDiff<int>.Set(0, 8),
            new VectorDiff<int>.Remove(1),
            new VectorDiff<int>.PopFront(),
            new VectorDiff<int>.PopBack(),
            new VectorDiff<int>.Truncate(1),
            new VectorDiff<int>.PushBack(7),
            new VectorDiff<int>.Clear()
        );

        // Assert
        await Assert
            .That(events)
            .IsEquivalentTo(
                [
                    "Reset -> 1,2,3",
                    "Add 4 at 3 -> 1,2,3,4",
                    "Add 5 at 4 -> 1,2,3,4,5",
                    "Add 0 at 0 -> 0,1,2,3,4,5",
                    "Add 9 at 2 -> 0,1,9,2,3,4,5",
                    "Replace 0 with 8 at 0 -> 8,1,9,2,3,4,5",
                    "Remove 1 at 1 -> 8,9,2,3,4,5",
                    "Remove 8 at 0 -> 9,2,3,4,5",
                    "Remove 5 at 4 -> 9,2,3,4",
                    "Remove 4 at 3 -> 9,2,3",
                    "Remove 3 at 2 -> 9,2",
                    "Remove 2 at 1 -> 9",
                    "Add 7 at 1 -> 9,7",
                    "Reset -> ",
                ],
                CollectionOrdering.Matching
            );
    }

    [Test]
    public async Task Batch_ShouldRaiseChangedOnceAfterAllDiffs()
    {
        // Arrange
        DiffSource<int> source = new();
        await using LiveList<int> list = source.Diffs.ToLiveList(ThreadPool);
        List<int[]> snapshots = [];
        list.Changed += (_, _) => snapshots.Add(list.ToArray());

        // Act
        await source.SendAsync(list, new VectorDiff<int>.Reset([1]), new VectorDiff<int>.PushBack(2));
        await source.SendAsync(list, new VectorDiff<int>.PopFront());

        // Assert
        await Assert.That(snapshots.Select(snapshot => string.Join(",", snapshot))).IsEquivalentTo(["1,2", "2"]);
    }

    [Test]
    public async Task PropertyChanged_ShouldReportCountOnlyWhenItChanged()
    {
        // Arrange
        DiffSource<int> source = new();
        await using LiveList<int> list = source.Diffs.ToLiveList(ThreadPool);
        List<string> properties = [];
        list.PropertyChanged += (_, e) => properties.Add(e.PropertyName ?? "");

        // Act
        await source.SendAsync(list, new VectorDiff<int>.PushBack(1), new VectorDiff<int>.Set(0, 2));

        // Assert
        await Assert.That(properties).IsEquivalentTo(["Count", "Item[]", "Item[]"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Initialized_ShouldCompleteAfterTheFirstBatch()
    {
        // Arrange
        DiffSource<int> source = new();
        await using LiveList<int> list = source.Diffs.ToLiveList(ThreadPool);
        bool initializedEarly = list.Initialized.IsCompleted;

        // Act
        source.Write(new VectorDiff<int>.Reset([1, 2]));
        await list.Initialized.WaitAsync(Poll.DefaultTimeout);

        // Assert
        await Assert.That(initializedEarly).IsFalse();
        await Assert.That(list.ToArray()).IsEquivalentTo([1, 2], CollectionOrdering.Matching);
    }

    [Test]
    public async Task StreamThrowingBeforeTheFirstBatch_ShouldFailInitializedAndCompletion()
    {
        // Arrange
        DiffSource<int> source = new();
        await using LiveList<int> list = source.Diffs.ToLiveList(ThreadPool);

        // Act
        source.Complete(new InvalidOperationException("subscribing failed"));

        // Assert
        await Assert.That(() => list.Initialized.WaitAsync(Poll.DefaultTimeout)).Throws<InvalidOperationException>();
        await Assert
            .That(() => list.Completion.WaitAsync(Poll.DefaultTimeout))
            .Throws<InvalidOperationException>()
            .WithMessage("subscribing failed");
    }

    [Test]
    public async Task StreamEndingWithoutABatch_ShouldFailInitializedAndCompleteCompletion()
    {
        // Arrange
        DiffSource<int> source = new();
        await using LiveList<int> list = source.Diffs.ToLiveList(ThreadPool);

        // Act
        source.Complete();
        await list.Completion.WaitAsync(Poll.DefaultTimeout);

        // Assert
        await Assert.That(list.Initialized.Exception?.InnerException).IsTypeOf<InvalidOperationException>();
    }

    [Test]
    public async Task CancelledStream_ShouldCancelCompletion()
    {
        // Arrange
        using CancellationTokenSource cancellation = new();
        DiffSource<int> source = new();
        await using LiveList<int> list = source.Diffs.WithToken(cancellation.Token).ToLiveList(ThreadPool);

        // Act
        await cancellation.CancelAsync();

        // Assert
        await Assert.That(() => list.Completion.WaitAsync(Poll.DefaultTimeout)).Throws<OperationCanceledException>();
        await Assert.That(list.Completion.IsCanceled).IsTrue();
    }

    [Test]
    public async Task RemovedItems_ShouldBeDisposedAfterTheirEvents()
    {
        // Arrange
        DiffSource<Disposable> source = new();
        Disposable[] items = [new(1), new(2), new(3)];
        Disposable replacement = new(4);
        await using LiveList<Disposable> list = source.Diffs.ToLiveList(ThreadPool);
        List<bool> disposedDuringEvent = [];
        list.CollectionChanged += (_, e) =>
            disposedDuringEvent.AddRange(e.OldItems?.Cast<Disposable>().Select(item => item.IsDisposed) ?? []);

        // Act
        await source.SendAsync(list, new VectorDiff<Disposable>.Reset(items));
        await source.SendAsync(
            list,
            new VectorDiff<Disposable>.Set(0, replacement),
            new VectorDiff<Disposable>.Remove(1)
        );

        // Assert
        await Assert.That(disposedDuringEvent).IsEquivalentTo([false, false]);
        await Assert
            .That(items.Select(item => item.IsDisposed))
            .IsEquivalentTo([true, true, false], CollectionOrdering.Matching);
        await Assert.That(replacement.IsDisposed).IsFalse();
    }

    [Test]
    public async Task Reset_ShouldDisposeTheItemsItReplaced()
    {
        // Arrange
        DiffSource<Disposable> source = new();
        Disposable old = new(1);
        Disposable current = new(2);
        await using LiveList<Disposable> list = source.Diffs.ToLiveList(ThreadPool);

        // Act
        await source.SendAsync(list, new VectorDiff<Disposable>.Reset([old]));
        await source.SendAsync(list, new VectorDiff<Disposable>.Reset([current]));

        // Assert
        await Assert.That(old.IsDisposed).IsTrue();
        await Assert.That(current.IsDisposed).IsFalse();
    }

    [Test]
    public async Task DisposeAsync_ShouldEndTheStreamResetAndDisposeTheItems()
    {
        // Arrange
        DiffSource<Disposable> source = new();
        Disposable[] items = [new(1), new(2)];
        LiveList<Disposable> list = source.Diffs.ToLiveList(ThreadPool);
        await source.SendAsync(list, new VectorDiff<Disposable>.Reset(items));
        List<NotifyCollectionChangedAction> actions = [];
        list.CollectionChanged += (_, e) => actions.Add(e.Action);

        // Act
        await list.DisposeAsync();

        // Assert
        await Assert.That(source.Ended).IsTrue();
        await Assert.That(list.Count).IsEqualTo(0);
        await Assert.That(actions).IsEquivalentTo([NotifyCollectionChangedAction.Reset]);
        await Assert.That(items.All(item => item.IsDisposed)).IsTrue();
        await Assert.That(list.Completion.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    public async Task DisposeAsync_ShouldBeIdempotent()
    {
        // Arrange
        DiffSource<int> source = new();
        LiveList<int> list = source.Diffs.ToLiveList(ThreadPool);

        // Act
        await list.DisposeAsync();
        await list.DisposeAsync();

        // Assert
        await Assert.That(source.Ended).IsTrue();
    }

    [Test]
    public async Task DisposeAsyncInAnEventHandler_ShouldStopTheBatchAndDisposeTheRest()
    {
        // Arrange
        DiffSource<Disposable> source = new();
        Disposable first = new(1);
        Disposable second = new(2);
        LiveList<Disposable> list = source.Diffs.ToLiveList(ThreadPool);
        Task? disposing = null;
        list.CollectionChanged += (_, _) => disposing ??= list.DisposeAsync().AsTask();

        // Act
        source.Write(new VectorDiff<Disposable>.PushBack(first), new VectorDiff<Disposable>.PushBack(second));
        await Poll.UntilAsync(() => Task.FromResult(disposing is not null), "the list is disposed");
        await disposing!.WaitAsync(Poll.DefaultTimeout);

        // Assert
        await Assert.That(first.IsDisposed).IsTrue();
        await Assert.That(second.IsDisposed).IsTrue();
        await Assert.That(list.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Selector_ShouldDisposeTheSourcesRightAway()
    {
        // Arrange
        DiffSource<Disposable> source = new();
        Disposable[] values = [new(1), new(2)];
        List<bool> disposedInSelector = [];
        await using LiveList<int> list = source.Diffs.ToLiveList(
            value =>
            {
                disposedInSelector.Add(value.IsDisposed);
                return value.Id * 10;
            },
            synchronizationContext: ThreadPool
        );

        // Act
        await source.SendAsync(list, new VectorDiff<Disposable>.Reset(values));

        // Assert
        await Assert.That(list.ToArray()).IsEquivalentTo([10, 20], CollectionOrdering.Matching);
        await Assert.That(disposedInSelector).IsEquivalentTo([false, false]);
        await Assert.That(values.All(value => value.IsDisposed)).IsTrue();
    }

    [Test]
    public async Task Update_ShouldChangeTheItemInPlace()
    {
        // Arrange
        DiffSource<Disposable> source = new();
        Disposable value = new(2);
        await using LiveList<ViewModel> list = source.Diffs.ToLiveList(
            created => new ViewModel { Id = created.Id },
            (item, updated) => item.Id = updated.Id,
            ThreadPool
        );
        await source.SendAsync(list, new VectorDiff<Disposable>.Reset([new Disposable(1)]));
        ViewModel item = list[0];
        List<NotifyCollectionChangedAction> actions = [];
        list.CollectionChanged += (_, e) => actions.Add(e.Action);

        // Act
        await source.SendAsync(list, new VectorDiff<Disposable>.Set(0, value));

        // Assert
        await Assert.That(list[0]).IsSameReferenceAs(item);
        await Assert.That(item.Id).IsEqualTo(2);
        await Assert.That(actions).IsEmpty();
        await Assert.That(value.IsDisposed).IsTrue();
    }

    [Test]
    public async Task AsyncSelector_ShouldProjectInOrder()
    {
        // Arrange
        DiffSource<int> source = new();
        await using LiveList<string> list = source.Diffs.ToLiveList(
            async (value, cancellationToken) =>
            {
                // later values finish first, the list keeps the order of the diffs anyway
                await Task.Delay(TimeSpan.FromMilliseconds(30 - (value * 10)), cancellationToken);
                return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            },
            synchronizationContext: ThreadPool
        );

        // Act
        await source.SendAsync(list, new VectorDiff<int>.Reset([1, 2]), new VectorDiff<int>.PushFront(0));

        // Assert
        await Assert.That(list.ToArray()).IsEquivalentTo(["0", "1", "2"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ThrowingSelector_ShouldFailCompletionAndDisposeTheValues()
    {
        // Arrange
        DiffSource<Disposable> source = new();
        Disposable[] values = [new(1), new(2), new(3)];
        Disposable next = new(4);
        await using LiveList<int> list = source.Diffs.ToLiveList(
            value => value.Id == 2 ? throw new FormatException("projection failed") : value.Id,
            synchronizationContext: ThreadPool
        );

        // Act
        source.Write(new VectorDiff<Disposable>.Reset(values), new VectorDiff<Disposable>.PushBack(next));

        // Assert
        await Assert.That(() => list.Completion.WaitAsync(Poll.DefaultTimeout)).Throws<FormatException>();
        await Assert.That(values.Append(next).All(value => value.IsDisposed)).IsTrue();
        await Assert.That(source.Ended).IsTrue();
    }

    [Test]
    public async Task DiffNotFittingTheList_ShouldFailCompletionAndDisposeItsValue()
    {
        // Arrange
        DiffSource<Disposable> source = new();
        Disposable value = new(1);
        await using LiveList<Disposable> list = source.Diffs.ToLiveList(ThreadPool);

        // Act
        source.Write(new VectorDiff<Disposable>.Insert(1, value));

        // Assert
        await Assert.That(() => list.Completion.WaitAsync(Poll.DefaultTimeout)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(value.IsDisposed).IsTrue();
    }

    [Test]
    public async Task Context_ShouldBeUsedForProjectionsAndEvents()
    {
        // Arrange
        using SingleThreadSynchronizationContext context = new();
        DiffSource<int> source = new();
        ConcurrentBag<int> threads = [];
        await using LiveList<int> list = source.Diffs.ToLiveList(
            async (value, _) =>
            {
                threads.Add(Environment.CurrentManagedThreadId);
                await Task.Yield();
                threads.Add(Environment.CurrentManagedThreadId);
                return value;
            },
            synchronizationContext: context
        );
        list.CollectionChanged += (_, _) => threads.Add(Environment.CurrentManagedThreadId);
        list.Changed += (_, _) => threads.Add(Environment.CurrentManagedThreadId);

        // Act
        await source.SendAsync(list, new VectorDiff<int>.Reset([1]), new VectorDiff<int>.PushBack(2));

        // Assert
        await Assert.That(threads.Distinct()).IsEquivalentTo([context.ThreadId]);
    }

    [Test]
    public async Task CurrentContext_ShouldBeTheDefault()
    {
        // Arrange
        using SingleThreadSynchronizationContext context = new();
        DiffSource<int> source = new();
        int thread = 0;

        // Act
        LiveList<int> list = await context.RunAsync(() => source.Diffs.ToLiveList());
        list.Changed += (_, _) => thread = Environment.CurrentManagedThreadId;
        await source.SendAsync(list, new VectorDiff<int>.Reset([1]));
        await list.DisposeAsync();

        // Assert
        await Assert.That(thread).IsEqualTo(context.ThreadId);
    }

    [Test]
    public async Task List_ShouldBeReadOnly()
    {
        // Arrange
        DiffSource<int> source = new();
        await using LiveList<int> list = source.Diffs.ToLiveList(ThreadPool);
        await source.SendAsync(list, new VectorDiff<int>.Reset([1, 2]));
        IList<int> generic = list;
        IList nonGeneric = list;

        // Act & Assert
        await Assert.That(generic.IsReadOnly).IsTrue();
        await Assert.That(nonGeneric.IsReadOnly).IsTrue();
        await Assert.That(() => generic.Add(3)).Throws<NotSupportedException>();
        await Assert.That(() => nonGeneric.RemoveAt(0)).Throws<NotSupportedException>();
        await Assert.That(nonGeneric.IndexOf(2)).IsEqualTo(1);
        await Assert.That(nonGeneric.Contains("2")).IsFalse();
        // WPF offers adding and removing rows for lists that aren't fixed size
        await Assert.That(nonGeneric.IsFixedSize).IsTrue();
    }

    [Test]
    public async Task DisposeAsyncAfterTheStreamEnded_ShouldNotBlockReadersDuringItsEvents()
    {
        // Arrange
        DiffSource<int> source = new();
        LiveList<int> list = source.Diffs.ToLiveList(ThreadPool);
        await source.SendAsync(list, new VectorDiff<int>.Reset([1, 2]));
        source.Complete();
        await list.Completion.WaitAsync(Poll.DefaultTimeout);
        bool readOnAnotherThread = false;
        list.CollectionChanged += (_, _) =>
#pragma warning disable VSTHRD002 // a handler waiting for another thread, the case that must not deadlock
            readOnAnotherThread = Task.Run(() => list.Count).Wait(TimeSpan.FromSeconds(5));
#pragma warning restore VSTHRD002

        // Act
        await list.DisposeAsync();

        // Assert
        await Assert.That(readOnAnotherThread).IsTrue();
    }

    [Test]
    public async Task ThrowingHandlerWhileDisposing_ShouldStillDisposeTheItems()
    {
        // Arrange
        DiffSource<Disposable> source = new();
        Disposable[] items = [new(1), new(2)];
        LiveList<Disposable> list = source.Diffs.ToLiveList(ThreadPool);
        await source.SendAsync(list, new VectorDiff<Disposable>.Reset(items));
        list.CollectionChanged += (_, _) => throw new FormatException("handler failed");

        // Act & Assert
        await Assert.That(async () => await list.DisposeAsync()).Throws<FormatException>();
        await Assert.That(items.All(item => item.IsDisposed)).IsTrue();
        await Assert.That(list.Count).IsEqualTo(0);
    }

    [Test]
    public async Task ThrowingHandlerDuringAppend_ShouldDisposeTheItemsNotInTheList()
    {
        // Arrange
        DiffSource<Disposable> source = new();
        Disposable[] items = [new(1), new(2), new(3)];
        await using LiveList<Disposable> list = source.Diffs.ToLiveList(ThreadPool);
        list.CollectionChanged += (_, e) =>
        {
            if (e.NewItems?[0] == items[0])
            {
                throw new FormatException("handler failed");
            }
        };

        // Act
        source.Write(new VectorDiff<Disposable>.Append(items));

        // Assert
        await Assert.That(() => list.Completion.WaitAsync(Poll.DefaultTimeout)).Throws<FormatException>();
        await Assert
            .That(items.Select(item => item.IsDisposed))
            .IsEquivalentTo([false, true, true], CollectionOrdering.Matching);
        await Assert.That(list.ToArray()).IsEquivalentTo([items[0]]);
    }

    [Test]
    public async Task SelectorReturningATask_ShouldBeRejected()
    {
        // Arrange
        DiffSource<int> source = new();

        // Act & Assert
        await Assert
            .That(() =>
                source.Diffs.ToLiveList(async value => await Task.FromResult(value), synchronizationContext: ThreadPool)
            )
            .Throws<ArgumentException>()
            .WithParameterName("selector");
    }

    [Test]
    public async Task AsyncSelector_ShouldProjectTheValuesOfADiffConcurrently()
    {
        // Arrange
        DiffSource<int> source = new();
        TaskCompletionSource allStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        await using LiveList<int> list = source.Diffs.ToLiveList(
            async (value, cancellationToken) =>
            {
                // sequential projections would wait here forever
                if (Interlocked.Increment(ref started) == 3)
                {
                    allStarted.SetResult();
                }
                await allStarted.Task.WaitAsync(cancellationToken);
                return value;
            },
            synchronizationContext: ThreadPool
        );

        // Act
        await source.SendAsync(list, new VectorDiff<int>.Reset([1, 2, 3]));

        // Assert
        await Assert.That(list.ToArray()).IsEquivalentTo([1, 2, 3], CollectionOrdering.Matching);
    }

    [Test]
    public async Task FailingAsyncSelector_ShouldDisposeTheOtherProjections()
    {
        // Arrange
        DiffSource<Disposable> source = new();
        Disposable[] values = [new(1), new(2), new(3)];
        List<Disposable> projections = [];
        await using LiveList<Disposable> list = source.Diffs.ToLiveList(
            async (value, cancellationToken) =>
            {
                await Task.Yield();
                if (value.Id == 2)
                {
                    throw new FormatException("projection failed");
                }
                Disposable projection = new(value.Id * 10);
                lock (projections)
                {
                    projections.Add(projection);
                }
                return projection;
            },
            synchronizationContext: ThreadPool
        );

        // Act
        source.Write(new VectorDiff<Disposable>.Reset(values));

        // Assert
        await Assert.That(() => list.Completion.WaitAsync(Poll.DefaultTimeout)).Throws<FormatException>();
        await Assert.That(values.All(value => value.IsDisposed)).IsTrue();
        await Assert.That(projections.All(projection => projection.IsDisposed)).IsTrue();
        await Assert.That(list.Count).IsEqualTo(0);
    }

    private static string Describe(NotifyCollectionChangedEventArgs e) =>
        e.Action switch
        {
            NotifyCollectionChangedAction.Add => $"Add {e.NewItems![0]} at {e.NewStartingIndex}",
            NotifyCollectionChangedAction.Remove => $"Remove {e.OldItems![0]} at {e.OldStartingIndex}",
            NotifyCollectionChangedAction.Replace =>
                $"Replace {e.OldItems![0]} with {e.NewItems![0]} at {e.NewStartingIndex}",
            _ => e.Action.ToString(),
        };

    private sealed class ViewModel
    {
        public int Id { get; set; }
    }

    private sealed class Disposable(int id) : IDisposable
    {
        public int Id { get; } = id;

        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;

        public override string ToString() => Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A diff stream fed by the test.
    /// </summary>
    private sealed class DiffSource<T>
    {
        private readonly Channel<VectorDiff<T>[]> _channel = Channel.CreateUnbounded<VectorDiff<T>[]>();
        private volatile bool _ended;

        public IAsyncEnumerable<VectorDiff<T>[]> Diffs => ReadAsync();

        /// <summary>
        /// Whether the enumeration of <see cref="Diffs"/> ended, the subscription of a real stream would end then.
        /// </summary>
        public bool Ended => _ended;

        public void Write(params VectorDiff<T>[] batch) => _channel.Writer.TryWrite(batch);

        public void Complete(Exception? error = null) => _channel.Writer.TryComplete(error);

        /// <summary>
        /// Sends a batch and waits until the list applied it.
        /// </summary>
        public async Task SendAsync<TItem>(LiveList<TItem> list, params VectorDiff<T>[] batch)
        {
            TaskCompletionSource applied = new(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged(object? sender, EventArgs e) => applied.TrySetResult();
            list.Changed += OnChanged;
            Write(batch);
            await applied.Task.WaitAsync(Poll.DefaultTimeout);
            list.Changed -= OnChanged;
        }

        private async IAsyncEnumerable<VectorDiff<T>[]> ReadAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default
        )
        {
            try
            {
                await foreach (VectorDiff<T>[] batch in _channel.Reader.ReadAllAsync(cancellationToken))
                {
                    yield return batch;
                }
            }
            finally
            {
                _ended = true;
            }
        }
    }
}

/// <summary>
/// Passes a token to the enumeration like <c>WatchItemDiffsAsync(cancellationToken)</c> does.
/// </summary>
file static class AsyncEnumerableExtensions
{
    public static async IAsyncEnumerable<T> WithToken<T>(
        this IAsyncEnumerable<T> source,
        CancellationToken token,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            token,
            cancellationToken
        );
        await foreach (T value in source.WithCancellation(linked.Token))
        {
            yield return value;
        }
    }
}

/// <summary>
/// A context running everything on one thread, like the UI thread of an app.
/// </summary>
file sealed class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = [];
    private readonly Thread _thread;

    public SingleThreadSynchronizationContext()
    {
        _thread = new Thread(Run) { IsBackground = true };
        _thread.Start();
    }

    public int ThreadId => _thread.ManagedThreadId;

    public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

    public override void Send(SendOrPostCallback d, object? state) => throw new NotSupportedException();

    public override SynchronizationContext CreateCopy() => this;

    /// <summary>
    /// Runs <paramref name="function"/> on the thread of the context.
    /// </summary>
    public Task<T> RunAsync<T>(Func<T> function)
    {
        TaskCompletionSource<T> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(_ => result.SetResult(function()), null);
        return result.Task;
    }

    public void Dispose() => _queue.CompleteAdding();

    private void Run()
    {
        SetSynchronizationContext(this);
        foreach ((SendOrPostCallback callback, object? state) in _queue.GetConsumingEnumerable())
        {
            callback(state);
        }
    }
}

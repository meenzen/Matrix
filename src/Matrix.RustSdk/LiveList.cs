using System.Collections;
using System.Collections.Specialized;
using System.ComponentModel;
using Matrix.RustSdk.Subscriptions;

namespace Matrix.RustSdk;

/// <summary>
/// A read only list kept up to date from a stream of <see cref="VectorDiff{T}"/> batches, for binding to a UI or
/// reading snapshots. Created with <see cref="LiveListExtensions"/>.
/// </summary>
/// <remarks>
/// <para>
/// The list enumerates the stream in the background, on the <see cref="SynchronizationContext"/> it was created with:
/// the diffs are projected and applied and the events are raised there, one diff at a time. Bind it to WPF, Avalonia or
/// MAUI controls only when it was created on their UI thread. Without a context the list is updated on thread pool
/// threads, use <see cref="Changed"/> and <see cref="ToArray"/> then: the list doesn't change while the handlers of
/// its events run. A list on another context than the current one can change before its handlers are attached, read
/// it once after attaching them. Reading the list (<see cref="Count"/>, the indexer, <see cref="ToArray"/>) is safe
/// from every thread, using the items isn't, see below.
/// </para>
/// <para>
/// The list owns its items: an item that leaves the list (<see cref="VectorDiff{T}.Set"/>,
/// <see cref="VectorDiff{T}.Remove"/>, <see cref="VectorDiff{T}.Truncate"/>, <see cref="VectorDiff{T}.Reset"/>, ...)
/// is disposed after the events for it were raised if it is <see cref="IDisposable"/>, the remaining ones when the
/// list is disposed. Items holding native objects (<c>Room</c>, <c>TimelineItem</c>) are only valid while they are in
/// the list: use them on the context of the list or in its event handlers, a snapshot taken elsewhere can contain
/// items that are disposed a moment later. The SDK replaces items often (a room on every new message), and moves them
/// with <see cref="VectorDiff{T}.Remove"/> and <see cref="VectorDiff{T}.Insert"/>: don't keep them, project them to
/// managed values instead, and don't share projections between lists or positions.
/// </para>
/// <para>
/// The list stops when the stream ends, throws or the list is disposed, see <see cref="Completion"/>. It keeps its
/// items then until it is disposed.
/// </para>
/// </remarks>
/// <typeparam name="T">The type of the items.</typeparam>
public sealed class LiveList<T>
    : IList<T>,
        IReadOnlyList<T>,
        IList,
        INotifyCollectionChanged,
        INotifyPropertyChanged,
        IAsyncDisposable
{
    private static readonly PropertyChangedEventArgs CountChangedArgs = new(nameof(Count));

    // the name WPF and ObservableCollection use for changes of the indexer
    private static readonly PropertyChangedEventArgs IndexerChangedArgs = new("Item[]");

    private static readonly NotifyCollectionChangedEventArgs ResetArgs = new(NotifyCollectionChangedAction.Reset);

    // only the pump changes the items, the lock protects readers on other threads
    private readonly Lock _lock = new();
    private readonly List<T> _items = [];
    private readonly SynchronizationContext? _context;
    private readonly CancellationTokenSource _disposal = new();
    private readonly TaskCompletionSource _initialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _disposed;

    private LiveList(SynchronizationContext? context)
    {
        _context = context;
        // Initialized fails together with Completion, only one of them has to be observed
        _ = _initialized.Task.ContinueWith(
            static task => task.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );
    }

    /// <summary>
    /// Raised for every change of the list, after the change: <see cref="NotifyCollectionChangedAction.Add"/>,
    /// <see cref="NotifyCollectionChangedAction.Remove"/> and <see cref="NotifyCollectionChangedAction.Replace"/> for a
    /// single item each (WPF doesn't support ranges), <see cref="NotifyCollectionChangedAction.Reset"/> for
    /// <see cref="VectorDiff{T}.Clear"/>, <see cref="VectorDiff{T}.Reset"/> and disposing the list.
    /// </summary>
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>
    /// Raised for <see cref="Count"/> and the indexer (<c>Item[]</c>) when they changed, like
    /// <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/> does.
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Raised once per batch of diffs after all of them were applied, the moment to render a snapshot.
    /// </summary>
    public event EventHandler? Changed;

    /// <summary>
    /// The number of items.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    /// <summary>
    /// Completes when the first batch of diffs was applied, the items the SDK had when the stream started. That isn't
    /// necessarily everything: a room list can still be loading (<c>RoomList.WatchLoadingStateAsync</c>) and a timeline
    /// only contains what was paginated. Fails like <see cref="Completion"/> if the stream throws before, with an
    /// <see cref="InvalidOperationException"/> if it ended without a batch, and is cancelled if the list is disposed
    /// before.
    /// </summary>
    public Task Initialized => _initialized.Task;

    /// <summary>
    /// Completes when the list stopped updating: successfully when the stream ended (the SDK can end streams on its
    /// own) or the list was disposed, cancelled when the stream was cancelled, faulted when it, a projection or an
    /// event handler threw. Observe it, the list doesn't report errors otherwise.
    /// </summary>
    public Task Completion => _completion.Task;

    /// <summary>
    /// The item at <paramref name="index"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is out of range.</exception>
    public T this[int index]
    {
        get
        {
            lock (_lock)
            {
                return _items[index];
            }
        }
    }

    bool ICollection<T>.IsReadOnly => true;

    bool IList.IsReadOnly => true;

    // like ReadOnlyCollection<T>, WPF offers adding and removing rows for lists that aren't fixed size
    bool IList.IsFixedSize => true;

    bool ICollection.IsSynchronized => false;

    object ICollection.SyncRoot => this;

    T IList<T>.this[int index]
    {
        get => this[index];
        set => throw ReadOnly();
    }

    object? IList.this[int index]
    {
        get => this[index];
        set => throw ReadOnly();
    }

    /// <summary>
    /// A copy of the items.
    /// </summary>
    public T[] ToArray()
    {
        lock (_lock)
        {
            return [.. _items];
        }
    }

    /// <summary>
    /// Enumerates a copy of the items, changes during the enumeration don't affect it.
    /// </summary>
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)ToArray()).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Whether the list contains <paramref name="item"/>.
    /// </summary>
    public bool Contains(T item)
    {
        lock (_lock)
        {
            return _items.Contains(item);
        }
    }

    /// <summary>
    /// The index of <paramref name="item"/>, -1 if the list doesn't contain it.
    /// </summary>
    public int IndexOf(T item)
    {
        lock (_lock)
        {
            return _items.IndexOf(item);
        }
    }

    /// <summary>
    /// Copies the items to <paramref name="array"/>, starting at <paramref name="arrayIndex"/>.
    /// </summary>
    public void CopyTo(T[] array, int arrayIndex)
    {
        lock (_lock)
        {
            _items.CopyTo(array, arrayIndex);
        }
    }

    bool IList.Contains(object? value) => IsCompatible(value) && Contains((T)value!);

    int IList.IndexOf(object? value) => IsCompatible(value) ? IndexOf((T)value!) : -1;

    void ICollection.CopyTo(Array array, int index)
    {
        lock (_lock)
        {
            ((ICollection)_items).CopyTo(array, index);
        }
    }

    void ICollection<T>.Add(T item) => throw ReadOnly();

    void ICollection<T>.Clear() => throw ReadOnly();

    bool ICollection<T>.Remove(T item) => throw ReadOnly();

    void IList<T>.Insert(int index, T item) => throw ReadOnly();

    void IList<T>.RemoveAt(int index) => throw ReadOnly();

    int IList.Add(object? value) => throw ReadOnly();

    void IList.Clear() => throw ReadOnly();

    void IList.Insert(int index, object? value) => throw ReadOnly();

    void IList.Remove(object? value) => throw ReadOnly();

    void IList.RemoveAt(int index) => throw ReadOnly();

    /// <summary>
    /// Stops the list, which ends the subscription of the stream, then removes the items (raising
    /// <see cref="NotifyCollectionChangedAction.Reset"/> on the context of the list) and disposes them. Don't block on
    /// it on the context of the list, the list needs the context to stop, and don't await it in a projection, the list
    /// waits for the projection to stop.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        TaskCompletionSource? disposing = null;
        Task disposed;
        lock (_lock)
        {
            if (_disposed is null)
            {
                disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposed = disposing.Task;
            }
            disposed = _disposed;
        }
        // outside the lock, disposing runs the event handlers and frees native objects
        if (disposing is not null)
        {
            _ = DisposeCoreAsync(disposing);
        }
        return new ValueTask(disposed);
    }

    /// <summary>
    /// Starts a list applying <paramref name="diffs"/> with <paramref name="projection"/>.
    /// </summary>
    internal static LiveList<T> Start<TSource>(
        IAsyncEnumerable<IReadOnlyList<VectorDiff<TSource>>> diffs,
        LiveListProjection<TSource, T> projection,
        SynchronizationContext? context
    )
    {
        ArgumentNullException.ThrowIfNull(diffs);
        LiveList<T> list = new(context);
        if (context is null)
        {
            _ = Task.Run(() => list.RunAsync(diffs, projection));
        }
        else
        {
            // the awaits of the pump continue on the context it starts on. VSTHRD001 is about the main thread of
            // Visual Studio, the list runs on any context.
#pragma warning disable VSTHRD001
            context.Post(static state => _ = ((Func<Task>)state!)(), () => list.RunAsync(diffs, projection));
#pragma warning restore VSTHRD001
        }
        return list;
    }

    /// <summary>
    /// Disposes the list and completes <paramref name="disposed"/>, never throws.
    /// </summary>
    private async Task DisposeCoreAsync(TaskCompletionSource disposed)
    {
        try
        {
            await _disposal.CancelAsync().ConfigureAwait(false);
            // the pump completes it without waiting for the caller, its errors belong to whoever observes Completion
#pragma warning disable VSTHRD003
            await _completion.Task.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
#pragma warning restore VSTHRD003
            await InvokeOnContextAsync(() => ResetTo([])).ConfigureAwait(false);
            disposed.SetResult();
        }
        catch (Exception e)
        {
            // an event handler threw, ResetTo disposed the items anyway
            disposed.SetException(e);
        }
        finally
        {
            _disposal.Dispose();
        }
    }

    /// <summary>
    /// Enumerates the stream and completes <see cref="Completion"/>, never throws.
    /// </summary>
    private async Task RunAsync<TSource>(
        IAsyncEnumerable<IReadOnlyList<VectorDiff<TSource>>> diffs,
        LiveListProjection<TSource, T> projection
    )
    {
        CancellationToken cancellationToken = _disposal.Token;
        try
        {
            // no ConfigureAwait, the pump stays on the context of the list
#pragma warning disable CA2007
            await foreach (IReadOnlyList<VectorDiff<TSource>> batch in diffs.WithCancellation(cancellationToken))
#pragma warning restore CA2007
            {
                await ApplyAsync(batch, projection, cancellationToken);
                Changed?.Invoke(this, EventArgs.Empty);
                _initialized.TrySetResult();
            }
            _initialized.TrySetException(
                new InvalidOperationException("The stream ended before it delivered the first items.")
            );
            _completion.TrySetResult();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _initialized.TrySetCanceled(cancellationToken);
            _completion.TrySetResult();
        }
        catch (OperationCanceledException e)
        {
            _initialized.TrySetCanceled(e.CancellationToken);
            _completion.TrySetCanceled(e.CancellationToken);
        }
        catch (Exception e)
        {
            _initialized.TrySetException(e);
            _completion.TrySetException(e);
        }
    }

    /// <summary>
    /// Applies the diffs of a batch in order, the diffs that weren't applied are disposed.
    /// </summary>
    private async Task ApplyAsync<TSource>(
        IReadOnlyList<VectorDiff<TSource>> batch,
        LiveListProjection<TSource, T> projection,
        CancellationToken cancellationToken
    )
    {
        // the diffs from next on weren't handed to ApplyAsync, which disposes what it doesn't use itself
        int next = 0;
        try
        {
            while (next < batch.Count)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await ApplyAsync(batch[next++], projection, cancellationToken);
            }
        }
        finally
        {
            for (int i = next; i < batch.Count; i++)
            {
                batch[i].Dispose();
            }
        }
    }

#pragma warning disable CA2007 // the pump stays on the context of the list
    private async Task ApplyAsync<TSource>(
        VectorDiff<TSource> diff,
        LiveListProjection<TSource, T> projection,
        CancellationToken cancellationToken
    )
    {
        switch (diff)
        {
            case VectorDiff<TSource>.Append append:
                T[] appended = await projection.CreateAllAsync(append.Values, cancellationToken);
                int inserted = 0;
                try
                {
                    while (inserted < appended.Length)
                    {
                        Insert(Count, appended[inserted++]);
                    }
                }
                finally
                {
                    // an event handler threw, the items after it aren't in the list
                    DisposeItems(appended.AsSpan(inserted));
                }
                break;
            case VectorDiff<TSource>.Clear:
                ResetTo([]);
                break;
            case VectorDiff<TSource>.PushFront pushFront:
                Insert(0, await projection.CreateAsync(pushFront.Value, cancellationToken));
                break;
            case VectorDiff<TSource>.PushBack pushBack:
                Insert(Count, await projection.CreateAsync(pushBack.Value, cancellationToken));
                break;
            case VectorDiff<TSource>.PopFront:
                RemoveAt(Count > 0 ? 0 : throw Empty());
                break;
            case VectorDiff<TSource>.PopBack:
                RemoveAt(Count > 0 ? Count - 1 : throw Empty());
                break;
            case VectorDiff<TSource>.Insert insert:
                CheckIndex(insert, insert.Index, Count + 1);
                Insert(insert.Index, await projection.CreateAsync(insert.Value, cancellationToken));
                break;
            case VectorDiff<TSource>.Set set:
                CheckIndex(set, set.Index, Count);
                if (!await projection.UpdateAsync(this[set.Index], set.Value, cancellationToken))
                {
                    Replace(set.Index, await projection.CreateAsync(set.Value, cancellationToken));
                }
                break;
            case VectorDiff<TSource>.Remove remove:
                CheckIndex(remove, remove.Index, Count);
                RemoveAt(remove.Index);
                break;
            case VectorDiff<TSource>.Truncate truncate:
                CheckIndex(truncate, truncate.Length, Count + 1);
                for (int i = Count - 1; i >= truncate.Length; i--)
                {
                    RemoveAt(i);
                }
                break;
            case VectorDiff<TSource>.Reset reset:
                ResetTo(await projection.CreateAllAsync(reset.Values, cancellationToken));
                break;
        }
    }
#pragma warning restore CA2007

    private void Insert(int index, T item)
    {
        lock (_lock)
        {
            _items.Insert(index, item);
        }
        RaiseChanged(new(NotifyCollectionChangedAction.Add, item, index), countChanged: true);
    }

    private void Replace(int index, T item)
    {
        T old;
        lock (_lock)
        {
            old = _items[index];
            _items[index] = item;
        }
        try
        {
            RaiseChanged(new(NotifyCollectionChangedAction.Replace, item, old, index), countChanged: false);
        }
        finally
        {
            SubscriptionStream.Dispose(old);
        }
    }

    private void RemoveAt(int index)
    {
        T old;
        lock (_lock)
        {
            old = _items[index];
            _items.RemoveAt(index);
        }
        try
        {
            RaiseChanged(new(NotifyCollectionChangedAction.Remove, old, index), countChanged: true);
        }
        finally
        {
            SubscriptionStream.Dispose(old);
        }
    }

    private void ResetTo(T[] items)
    {
        T[] old;
        lock (_lock)
        {
            old = [.. _items];
            _items.Clear();
            _items.AddRange(items);
        }
        if (old.Length == 0 && items.Length == 0)
        {
            return;
        }
        try
        {
            RaiseChanged(ResetArgs, countChanged: old.Length != items.Length);
        }
        finally
        {
            DisposeItems(old);
        }
    }

    private void RaiseChanged(NotifyCollectionChangedEventArgs args, bool countChanged)
    {
        CollectionChanged?.Invoke(this, args);
        if (countChanged)
        {
            PropertyChanged?.Invoke(this, CountChangedArgs);
        }
        PropertyChanged?.Invoke(this, IndexerChangedArgs);
    }

    private Task InvokeOnContextAsync(Action action)
    {
        if (_context is null || SynchronizationContext.Current == _context)
        {
            action();
            return Task.CompletedTask;
        }
        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
#pragma warning disable VSTHRD001 // see Start
        _context.Post(
            _ =>
            {
                try
                {
                    action();
                    done.SetResult();
                }
                catch (Exception e)
                {
                    done.SetException(e);
                }
            },
            null
        );
#pragma warning restore VSTHRD001
        return done.Task;
    }

    private static void DisposeItems(ReadOnlySpan<T> items)
    {
        foreach (T item in items)
        {
            SubscriptionStream.Dispose(item);
        }
    }

    private static void CheckIndex<TSource>(VectorDiff<TSource> diff, int index, int limit)
    {
        if (index < 0 || index >= limit)
        {
            diff.Dispose();
            throw new ArgumentOutOfRangeException(
                nameof(diff),
                diff,
                "The diff doesn't fit the list, the stream skipped a diff."
            );
        }
    }

    private static InvalidOperationException Empty() =>
        new("The diff removes an item from the empty list, the stream skipped a diff.");

    private static bool IsCompatible(object? value) => value is T || (value is null && default(T) is null);

    private static NotSupportedException ReadOnly() =>
        new("The list is read only, it changes with the diffs of the SDK.");
}

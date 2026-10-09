namespace Matrix.RustSdk;

/// <summary>
/// Creates <see cref="LiveList{T}"/>s from the diff streams of the SDK, like
/// <c>timeline.WatchItemDiffsAsync(cancellationToken).ToLiveList(item => Format(item))</c>.
/// </summary>
/// <remarks>
/// The list starts enumerating the stream right away on <c>synchronizationContext</c>, by default the current one
/// (<see cref="SynchronizationContext.Current"/>, the UI thread in WPF, Avalonia and MAUI, none in console apps). Pass
/// <c>new SynchronizationContext()</c> to update the list on thread pool threads from a UI thread. Dispose the list to
/// stop it, which also ends the subscription. See <see cref="LiveList{T}"/> for the ownership of the items.
/// </remarks>
public static class LiveListExtensions
{
    /// <summary>
    /// Keeps a list of the values of <paramref name="diffs"/> up to date. The list owns the values: they are disposed
    /// when they leave the list and when the list is disposed, don't keep references to them.
    /// </summary>
    /// <param name="diffs">The diffs, applied batch by batch.</param>
    /// <param name="synchronizationContext">The context to update the list on, by default the current one.</param>
    /// <typeparam name="T">The type of the items.</typeparam>
    public static LiveList<T> ToLiveList<T>(
        this IAsyncEnumerable<IReadOnlyList<VectorDiff<T>>> diffs,
        SynchronizationContext? synchronizationContext = null
    ) =>
        LiveList<T>.Start(
            diffs,
            OwningProjection<T>.Instance,
            synchronizationContext ?? SynchronizationContext.Current
        );

    /// <summary>
    /// Keeps a list of projections of the values of <paramref name="diffs"/> up to date. Every value is disposed right
    /// after <paramref name="selector"/> (or <paramref name="update"/>) returned, the projections must not keep it.
    /// The list owns the projections like <see cref="ToLiveList{T}"/> owns the values.
    /// </summary>
    /// <param name="diffs">The diffs, applied batch by batch.</param>
    /// <param name="selector">Projects a value, called on the context of the list.</param>
    /// <param name="update">
    /// Updates an item in place for <see cref="VectorDiff{T}.Set"/> instead of replacing it with a new projection, for
    /// view models that keep their identity (selection and focus in a UI). No collection change is raised then, the
    /// item has to notify about its changes itself. Called on the context of the list.
    /// </param>
    /// <param name="synchronizationContext">The context to update the list on, by default the current one.</param>
    /// <typeparam name="TSource">The type of the values of the diffs.</typeparam>
    /// <typeparam name="T">The type of the items.</typeparam>
    public static LiveList<T> ToLiveList<TSource, T>(
        this IAsyncEnumerable<IReadOnlyList<VectorDiff<TSource>>> diffs,
        Func<TSource, T> selector,
        Action<T, TSource>? update = null,
        SynchronizationContext? synchronizationContext = null
    )
    {
        ArgumentNullException.ThrowIfNull(selector);
        return LiveList<T>.Start(
            diffs,
            new SelectorProjection<TSource, T>(selector, update),
            synchronizationContext ?? SynchronizationContext.Current
        );
    }

    /// <summary>
    /// Keeps a list of projections of the values of <paramref name="diffs"/> up to date, with an asynchronous
    /// projection for data like <c>Room.RoomInfo()</c>. The values are projected one after another and applied in
    /// order. Every value is disposed right after <paramref name="selector"/> (or <paramref name="update"/>)
    /// completed, the projections must not keep it. The list owns the projections like <see cref="ToLiveList{T}"/>
    /// owns the values.
    /// </summary>
    /// <param name="diffs">The diffs, applied batch by batch.</param>
    /// <param name="selector">
    /// Projects a value, called on the context of the list. The token is cancelled when the list is disposed.
    /// </param>
    /// <param name="update">
    /// Updates an item in place for <see cref="VectorDiff{T}.Set"/> instead of replacing it with a new projection, for
    /// view models that keep their identity (selection and focus in a UI). No collection change is raised then, the
    /// item has to notify about its changes itself. Called on the context of the list.
    /// </param>
    /// <param name="synchronizationContext">The context to update the list on, by default the current one.</param>
    /// <typeparam name="TSource">The type of the values of the diffs.</typeparam>
    /// <typeparam name="T">The type of the items.</typeparam>
    public static LiveList<T> ToLiveList<TSource, T>(
        this IAsyncEnumerable<IReadOnlyList<VectorDiff<TSource>>> diffs,
        Func<TSource, CancellationToken, ValueTask<T>> selector,
        Func<T, TSource, CancellationToken, ValueTask>? update = null,
        SynchronizationContext? synchronizationContext = null
    )
    {
        ArgumentNullException.ThrowIfNull(selector);
        return LiveList<T>.Start(
            diffs,
            new AsyncSelectorProjection<TSource, T>(selector, update),
            synchronizationContext ?? SynchronizationContext.Current
        );
    }
}

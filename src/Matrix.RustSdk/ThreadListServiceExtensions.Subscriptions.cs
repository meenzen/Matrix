using Matrix.RustSdk.Bindings.Ui;
using Matrix.RustSdk.Subscriptions;

namespace Matrix.RustSdk.Bindings;

/// <summary>
/// Helpers for <see cref="ThreadListService"/>.
/// </summary>
public static partial class ThreadListServiceExtensions
{
    /// <summary>
    /// Watches the threads of the list. Yields the changes in batches, starting with a
    /// <see cref="ThreadListUpdate.Reset"/> containing the loaded threads.
    /// </summary>
    /// <param name="threadListService">The thread list service.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/disposable/*"/>
    /// <para>Disposing a diff disposes the threads it contains, don't dispose diffs whose threads you keep.</para>
    /// </remarks>
    [Subscription(nameof(ThreadListService.SubscribeToItemsUpdates), SubscriptionBuffer.All)]
    public static partial IAsyncEnumerable<ThreadListUpdate[]> WatchItemDiffsAsync(
        this ThreadListService threadListService,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the pagination of the thread list. Yields its state, starting with the current one.
    /// </summary>
    /// <param name="threadListService">The thread list service.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/></remarks>
    [Subscription(nameof(ThreadListService.SubscribeToPaginationStateUpdates), SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<ThreadListPaginationState> WatchPaginationStateAsync(
        this ThreadListService threadListService,
        CancellationToken cancellationToken = default
    );
}

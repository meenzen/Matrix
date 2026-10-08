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
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/disposable/*"/>
    /// </remarks>
    [Subscription(nameof(ThreadListService.SubscribeToItemsUpdates), SubscriptionBuffer.All)]
    public static partial IAsyncEnumerable<ThreadListUpdate[]> WatchItemDiffsAsync(
        this ThreadListService threadListService,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the pagination of the thread list. Yields its state, starting with the current one.
    /// </summary>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/></remarks>
    [Subscription(nameof(ThreadListService.SubscribeToPaginationStateUpdates), SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<ThreadListPaginationState> WatchPaginationStateAsync(
        this ThreadListService threadListService,
        CancellationToken cancellationToken = default
    );
}

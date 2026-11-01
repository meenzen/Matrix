using Matrix.RustSdk.Bindings.Ui;
using Matrix.RustSdk.Subscriptions;

namespace Matrix.RustSdk.Bindings;

/// <summary>
/// Helpers for <see cref="SearchService"/>.
/// </summary>
public static partial class SearchServiceExtensions
{
    /// <summary>
    /// Watches the pagination of the search. Yields its state, starting with the current one.
    /// </summary>
    /// <param name="searchService">The search service.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/></remarks>
    [Subscription(nameof(SearchService.SubscribeToPaginationStateUpdates), SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<SearchServicePaginationState> WatchPaginationStateAsync(
        this SearchService searchService,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the results of the search. Yields the changes in batches, starting with a
    /// <see cref="VectorDiff{T}.Reset"/>.
    /// </summary>
    /// <param name="searchService">The search service.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/diffs/*"/>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/disposable/*"/>
    /// <para>Disposing a diff disposes the results it contains, don't dispose diffs whose results you keep.</para>
    /// </remarks>
    [Subscription(nameof(SearchService.SubscribeToResults), SubscriptionBuffer.All)]
    public static partial IAsyncEnumerable<VectorDiff<SearchServiceResult>[]> WatchResultDiffsAsync(
        this SearchService searchService,
        CancellationToken cancellationToken = default
    );
}

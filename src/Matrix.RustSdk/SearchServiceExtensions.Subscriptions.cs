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
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/></remarks>
    [Subscription(nameof(SearchService.SubscribeToPaginationStateUpdates), SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<SearchServicePaginationState> WatchPaginationStateAsync(
        this SearchService searchService,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the results of the search. Yields the changes in batches, starting with a
    /// <see cref="SearchServiceResultsUpdate.Reset"/>.
    /// </summary>
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/disposable/*"/>
    /// </remarks>
    [Subscription(nameof(SearchService.SubscribeToResults), SubscriptionBuffer.All)]
    public static partial IAsyncEnumerable<SearchServiceResultsUpdate[]> WatchResultDiffsAsync(
        this SearchService searchService,
        CancellationToken cancellationToken = default
    );
}

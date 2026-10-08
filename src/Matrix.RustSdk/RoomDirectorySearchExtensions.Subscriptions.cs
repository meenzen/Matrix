using Matrix.RustSdk.Subscriptions;

namespace Matrix.RustSdk.Bindings;

/// <summary>
/// Helpers for <see cref="RoomDirectorySearch"/>.
/// </summary>
public static partial class RoomDirectorySearchExtensions
{
    /// <summary>
    /// Watches the results of the search. Yields the changes in batches, starting with a
    /// <see cref="RoomDirectorySearchEntryUpdate.Reset"/>.
    /// </summary>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/></remarks>
    [Subscription(nameof(RoomDirectorySearch.Results), SubscriptionBuffer.All)]
    public static partial IAsyncEnumerable<RoomDirectorySearchEntryUpdate[]> WatchResultDiffsAsync(
        this RoomDirectorySearch search,
        CancellationToken cancellationToken = default
    );
}

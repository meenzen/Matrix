using Matrix.RustSdk.Subscriptions;

namespace Matrix.RustSdk.Bindings;

/// <summary>
/// Helpers for <see cref="SyncService"/>.
/// </summary>
public static partial class SyncServiceExtensions
{
    /// <summary>
    /// Watches the state of the sync service. Yields it, starting with the current one.
    /// </summary>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/></remarks>
    [Subscription(nameof(SyncService.State), SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<SyncServiceState> WatchStateAsync(
        this SyncService syncService,
        CancellationToken cancellationToken = default
    );
}

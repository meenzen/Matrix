using Matrix.RustSdk.Subscriptions;

namespace Matrix.RustSdk.Bindings;

/// <summary>
/// Helpers for <see cref="LiveLocationsObserver"/>.
/// </summary>
public static partial class LiveLocationsObserverExtensions
{
    /// <summary>
    /// Watches the active live location shares of the room. Yields the changes in batches, starting with a
    /// <see cref="LiveLocationShareUpdate.Reset"/> of the current shares if there are any.
    /// </summary>
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/>
    /// <para>Disposing the observer ends the enumeration.</para>
    /// </remarks>
    [Subscription(nameof(LiveLocationsObserver.Subscribe), SubscriptionBuffer.All)]
    public static partial IAsyncEnumerable<LiveLocationShareUpdate[]> WatchLiveLocationShareDiffsAsync(
        this LiveLocationsObserver observer,
        CancellationToken cancellationToken = default
    );
}

using Matrix.RustSdk.Subscriptions;

namespace Matrix.RustSdk.Bindings;

/// <summary>
/// Helpers for <see cref="LiveLocationsObserver"/>.
/// </summary>
public static partial class LiveLocationsObserverExtensions
{
    /// <summary>
    /// Watches the active live location shares of the room. Yields the changes in batches, starting with a
    /// <see cref="VectorDiff{T}.Reset"/> of the current shares if there are any.
    /// </summary>
    /// <param name="liveLocationsObserver">The observer of the live locations of a room.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/diffs/*"/>
    /// <para>Disposing the observer ends the enumeration.</para>
    /// </remarks>
    [Subscription(nameof(LiveLocationsObserver.Subscribe), SubscriptionBuffer.All)]
    public static partial IAsyncEnumerable<VectorDiff<LiveLocationShare>[]> WatchLiveLocationShareDiffsAsync(
        this LiveLocationsObserver liveLocationsObserver,
        CancellationToken cancellationToken = default
    );
}

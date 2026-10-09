using Matrix.RustSdk.Bindings.Sdk;
using Matrix.RustSdk.Subscriptions;

namespace Matrix.RustSdk.Bindings;

/// <summary>
/// Helpers for <see cref="Timeline"/>.
/// </summary>
public static partial class TimelineExtensions
{
    /// <summary>
    /// Watches the items of the timeline. Yields the changes in batches, starting with a
    /// <see cref="VectorDiff{T}.Reset"/> containing the current items. Applying them to a list in order keeps a copy of
    /// the timeline.
    /// </summary>
    /// <param name="timeline">The timeline.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/disposable/*"/>
    /// <para>Disposing a diff disposes the items it contains, don't dispose diffs whose items you keep.</para>
    /// </remarks>
    [Subscription(nameof(Timeline.AddListener), SubscriptionBuffer.All)]
    public static partial IAsyncEnumerable<VectorDiff<TimelineItem>[]> WatchItemDiffsAsync(
        this Timeline timeline,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the back pagination of the live timeline. Yields its status, starting with the current one.
    /// </summary>
    /// <param name="timeline">The timeline.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/></remarks>
    /// <exception cref="ClientException">
    /// The timeline isn't a live timeline, thrown when the enumeration starts.
    /// </exception>
    [Subscription(nameof(Timeline.SubscribeToBackPaginationStatus), SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<PaginationStatus> WatchBackPaginationStatusAsync(
        this Timeline timeline,
        CancellationToken cancellationToken = default
    );
}

using Matrix.RustSdk.Subscriptions;

namespace Matrix.RustSdk.Bindings;

/// <summary>
/// Helpers for <see cref="SpaceService"/>.
/// </summary>
public static partial class SpaceServiceExtensions
{
    /// <summary>
    /// Watches the space filters. Yields the changes in batches, starting with a
    /// <see cref="SpaceFilterUpdate.Reset"/>.
    /// </summary>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/></remarks>
    [Subscription(nameof(SpaceService.SubscribeToSpaceFilters), SubscriptionBuffer.All)]
    public static partial IAsyncEnumerable<SpaceFilterUpdate[]> WatchSpaceFilterDiffsAsync(
        this SpaceService spaceService,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the joined spaces that aren't a child of another joined space. Yields the changes in batches, starting
    /// with a <see cref="SpaceListUpdate.Reset"/>.
    /// </summary>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/></remarks>
    [Subscription(nameof(SpaceService.SubscribeToTopLevelJoinedSpaces), SubscriptionBuffer.All)]
    public static partial IAsyncEnumerable<SpaceListUpdate[]> WatchTopLevelJoinedSpaceDiffsAsync(
        this SpaceService spaceService,
        CancellationToken cancellationToken = default
    );
}

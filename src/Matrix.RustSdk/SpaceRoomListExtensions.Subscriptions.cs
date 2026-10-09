using Matrix.RustSdk.Bindings.Ui;
using Matrix.RustSdk.Subscriptions;

namespace Matrix.RustSdk.Bindings;

/// <summary>
/// Helpers for <see cref="SpaceRoomList"/>.
/// </summary>
public static partial class SpaceRoomListExtensions
{
    /// <summary>
    /// Watches the pagination of the list. Yields its state, starting with the current one.
    /// </summary>
    /// <param name="spaceRoomList">The room list of the space.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/></remarks>
    [Subscription(nameof(SpaceRoomList.SubscribeToPaginationStateUpdates), SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<SpaceRoomListPaginationState> WatchPaginationStateAsync(
        this SpaceRoomList spaceRoomList,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the child rooms of the space. Yields the changes in batches, starting with a
    /// <see cref="SpaceListUpdate.Reset"/>.
    /// </summary>
    /// <param name="spaceRoomList">The room list of the space.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/></remarks>
    [Subscription(nameof(SpaceRoomList.SubscribeToRoomUpdate), SubscriptionBuffer.All)]
    public static partial IAsyncEnumerable<SpaceListUpdate[]> WatchRoomDiffsAsync(
        this SpaceRoomList spaceRoomList,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the space itself. Yields it, starting with the current value, <see langword="null"/> while it isn't
    /// known.
    /// </summary>
    /// <param name="spaceRoomList">The room list of the space.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/></remarks>
    [Subscription(nameof(SpaceRoomList.SubscribeToSpaceUpdates), SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<SpaceRoom?> WatchSpaceAsync(
        this SpaceRoomList spaceRoomList,
        CancellationToken cancellationToken = default
    );
}

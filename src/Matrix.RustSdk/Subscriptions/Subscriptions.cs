using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk.Subscriptions;

// The subscriptions of the bindings as IAsyncEnumerable, implemented by Matrix.RustSdk.Generators. Each enumeration
// subscribes when it starts and cancels the subscription when it ends (break, exception or cancellation).

public static partial class SyncServiceSubscriptions
{
    /// <summary>
    /// The state of the sync service, starting with the current one. Only the latest state is buffered.
    /// </summary>
    [Subscription(nameof(SyncService.State), Buffer = SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<SyncServiceState> StateChangesAsync(
        this SyncService syncService,
        CancellationToken cancellationToken = default
    );
}

public static partial class RoomListServiceSubscriptions
{
    /// <summary>
    /// Whether a sync indicator should be shown, delayed to avoid flickering. Only the latest value is buffered.
    /// </summary>
    [Subscription(nameof(RoomListService.SyncIndicator), Buffer = SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<RoomListServiceSyncIndicator> SyncIndicatorChangesAsync(
        this RoomListService roomListService,
        uint delayBeforeShowingInMs,
        uint delayBeforeHidingInMs,
        CancellationToken cancellationToken = default
    );
}

public static partial class TimelineSubscriptions
{
    /// <summary>
    /// The changes of the timeline, starting with a <see cref="TimelineDiff.Reset"/> containing the current items.
    /// Every batch is buffered, the consumer owns the diffs and has to dispose them.
    /// </summary>
    [Subscription(nameof(Timeline.AddListener))]
    public static partial IAsyncEnumerable<TimelineDiff[]> UpdatesAsync(
        this Timeline timeline,
        CancellationToken cancellationToken = default
    );
}

public static partial class RoomSubscriptions
{
    /// <summary>
    /// The room info whenever it changes. Only the latest info is buffered.
    /// </summary>
    [Subscription(nameof(Room.SubscribeToRoomInfoUpdates), Buffer = SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<RoomInfo> InfoUpdatesAsync(
        this Room room,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// The ids of the users currently typing in the room. Only the latest list is buffered.
    /// </summary>
    [Subscription(nameof(Room.SubscribeToTypingNotifications), Buffer = SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<string[]> TypingUsersAsync(
        this Room room,
        CancellationToken cancellationToken = default
    );
}

public static partial class ClientSubscriptions
{
    /// <summary>
    /// The info of the room <paramref name="roomId"/> whenever it changes. Only the latest info is buffered.
    /// </summary>
    [Subscription(nameof(Client.SubscribeToRoomInfo), Buffer = SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<RoomInfo> RoomInfoUpdatesAsync(
        this Client client,
        string roomId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// The send queue updates of all rooms. Every update is buffered, the consumer has to dispose them.
    /// </summary>
    [Subscription(nameof(Client.SubscribeToSendQueueUpdates))]
    public static partial IAsyncEnumerable<(string RoomId, RoomSendQueueUpdate Update)> SendQueueUpdatesAsync(
        this Client client,
        CancellationToken cancellationToken = default
    );
}

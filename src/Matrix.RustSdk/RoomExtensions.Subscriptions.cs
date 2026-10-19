using Matrix.RustSdk.Subscriptions;

namespace Matrix.RustSdk.Bindings;

/// <summary>
/// Helpers for <see cref="Room"/>.
/// </summary>
public static partial class RoomExtensions
{
    /// <summary>
    /// Watches who declines the call announced by the notification event <paramref name="rtcNotificationEventId"/>.
    /// Yields the user id of each member declining it, declines from before the enumeration started are missed.
    /// </summary>
    /// <param name="room">The room.</param>
    /// <param name="rtcNotificationEventId">The id of the event announcing the call.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/>
    /// <para>The SDK also ends the subscription when it falls behind, subscribe again if you still need it.</para>
    /// </remarks>
    /// <exception cref="ClientException">The event id is invalid, thrown when the enumeration starts.</exception>
    [Subscription(nameof(Room.SubscribeToCallDeclineEvents), SubscriptionBuffer.All)]
    public static partial IAsyncEnumerable<string> WatchCallDeclinesAsync(
        this Room room,
        string rtcNotificationEventId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the identity status of the room members. Yields the changes, starting with the members whose identity
    /// needs attention, if there are any.
    /// </summary>
    /// <param name="room">The room.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/></remarks>
    [Subscription(nameof(Room.SubscribeToIdentityStatusChanges), SubscriptionBuffer.All)]
    public static partial IAsyncEnumerable<IdentityStatusChange[]> WatchIdentityStatusChangesAsync(
        this Room room,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the requests to join the room. Yields all pending requests, starting with the current ones.
    /// </summary>
    /// <param name="room">The room.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/disposable/*"/>
    /// </remarks>
    [Subscription(nameof(Room.SubscribeToKnockRequests), SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<KnockRequest[]> WatchKnockRequestsAsync(
        this Room room,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the info of the room. Yields it, starting with the current info, then each time it changes.
    /// </summary>
    /// <param name="room">The room.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/disposable/*"/>
    /// </remarks>
    [Subscription(nameof(Room.SubscribeToRoomInfoUpdates), SubscriptionBuffer.Latest, Current = nameof(Room.RoomInfo))]
    public static partial IAsyncEnumerable<RoomInfo> WatchRoomInfoAsync(
        this Room room,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the send queue of the room. Yields each update, starting with a
    /// <see cref="RoomSendQueueUpdate.NewLocalEvent"/> for every local echo that wasn't sent yet.
    /// </summary>
    /// <param name="room">The room.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/disposable/*"/>
    /// <para>End the enumeration before disposing the client, the SDK keeps polling otherwise.</para>
    /// </remarks>
    [Subscription(nameof(Room.SubscribeToSendQueueUpdates), SubscriptionBuffer.All)]
    public static partial IAsyncEnumerable<RoomSendQueueUpdate> WatchSendQueueUpdatesAsync(
        this Room room,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches who is typing in the room. Yields the ids of the typing users without the own user each time it
    /// changes, starting with the next change.
    /// </summary>
    /// <param name="room">The room.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/>
    /// <para>The SDK also ends the subscription when it falls behind, subscribe again if you still need it.</para>
    /// </remarks>
    [Subscription(nameof(Room.SubscribeToTypingNotifications), SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<string[]> WatchTypingUsersAsync(
        this Room room,
        CancellationToken cancellationToken = default
    );
}

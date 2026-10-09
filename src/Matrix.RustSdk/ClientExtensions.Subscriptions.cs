using Matrix.RustSdk.Bindings.RumaEvents;
using Matrix.RustSdk.Subscriptions;

// the helpers are in the namespace of the types they extend, so they show up without another using
namespace Matrix.RustSdk.Bindings;

/// <summary>
/// Helpers for <see cref="Client"/>.
/// </summary>
public static partial class ClientExtensions
{
    /// <summary>
    /// Watches the global account data event of type <paramref name="eventType"/>. Yields the event each time a sync
    /// changes it, starting with the next change.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="eventType">The type of the account data event.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/></remarks>
    [Subscription(nameof(Client.ObserveAccountDataEvent), SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<AccountDataEvent> WatchAccountDataAsync(
        this Client client,
        AccountDataEventType eventType,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the account data event of type <paramref name="eventType"/> of the room <paramref name="roomId"/>.
    /// Yields the event each time a sync changes it, starting with the next change.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="roomId">The id of the room.</param>
    /// <param name="eventType">The type of the room account data event.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/></remarks>
    /// <exception cref="ClientException">The room id is invalid, thrown when the enumeration starts.</exception>
    public static IAsyncEnumerable<RoomAccountDataEvent> WatchRoomAccountDataAsync(
        this Client client,
        string roomId,
        RoomAccountDataEventType eventType,
        CancellationToken cancellationToken = default
    ) =>
        client.WatchRoomAccountDataWithRoomIdAsync(roomId, eventType, cancellationToken).Select(update => update.Event);

    // the listener also receives the room id, which the caller passed in anyway
    [Subscription(nameof(Client.ObserveRoomAccountDataEvent), SubscriptionBuffer.Latest)]
    internal static partial IAsyncEnumerable<(
        RoomAccountDataEvent Event,
        string RoomId
    )> WatchRoomAccountDataWithRoomIdAsync(
        this Client client,
        string roomId,
        RoomAccountDataEventType eventType,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Watches for failed uploads of one-time keys because the server already has a key with the same id. Yields the
    /// details of each failure, <see langword="null"/> if they couldn't be read from the error of the server.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/>
    /// <para>End the enumeration before disposing the client, the SDK keeps polling otherwise.</para>
    /// </remarks>
    [Subscription(nameof(Client.SubscribeToDuplicateKeyUploadErrors), SubscriptionBuffer.All)]
    public static partial IAsyncEnumerable<DuplicateOneTimeKeyErrorMessage?> WatchDuplicateKeyUploadErrorsAsync(
        this Client client,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the ignored users. Yields their ids, starting with the current list.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/></remarks>
    [Subscription(nameof(Client.SubscribeToIgnoredUsers), SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<string[]> WatchIgnoredUsersAsync(
        this Client client,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the media preview configuration of the account. Yields it, starting with the current one,
    /// <see langword="null"/> while none is set.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/></remarks>
    [Subscription(nameof(Client.SubscribeToMediaPreviewConfig), SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<MediaPreviewConfig?> WatchMediaPreviewConfigAsync(
        this Client client,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the live location shares (beacon info) of the own user in all rooms. Yields each change as it arrives
    /// with sync, starting with the next change.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/></remarks>
    [Subscription(nameof(Client.SubscribeToOwnBeaconInfoUpdates), SubscriptionBuffer.All)]
    public static partial IAsyncEnumerable<BeaconInfoUpdate> WatchOwnBeaconInfoUpdatesAsync(
        this Client client,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the profile of the own user. Yields it, starting with the stored one if there is one, then each time a
    /// sync changes it.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/>
    /// <para>The SDK also ends the subscription when it falls behind, subscribe again if you still need it.</para>
    /// </remarks>
    [Subscription(nameof(Client.SubscribeToOwnProfile), SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<UserProfile> WatchOwnProfileAsync(
        this Client client,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the info of the room <paramref name="roomId"/>, also for rooms the client doesn't know yet. Yields it
    /// each time a notable change happens, starting with the current info if the room is known.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="roomId">The id of the room.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/disposable/*"/>
    /// <para>The SDK also ends the subscription when it falls behind, subscribe again if you still need it.</para>
    /// </remarks>
    /// <exception cref="ClientException">The room id is invalid, thrown when the enumeration starts.</exception>
    [Subscription(nameof(Client.SubscribeToRoomInfo), SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<RoomInfo> WatchRoomInfoAsync(
        this Client client,
        string roomId,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the send queues of all rooms for errors. Yields the room id and the error each time a room fails to
    /// send an event.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/>
    /// <para>
    /// Subscribing restarts the send queues of rooms with unsent events. The subscription keeps the client alive until
    /// the enumeration ends.
    /// </para>
    /// </remarks>
    [Subscription(nameof(Client.SubscribeToSendQueueStatus), SubscriptionBuffer.All)]
    public static partial IAsyncEnumerable<(string RoomId, ClientException Error)> WatchSendQueueErrorsAsync(
        this Client client,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the send queues of all rooms. Yields the room id and each update, starting with a
    /// <see cref="RoomSendQueueUpdate.NewLocalEvent"/> for every local echo that wasn't sent yet.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/disposable/*"/>
    /// <para>End the enumeration before disposing the client, the SDK keeps polling otherwise.</para>
    /// </remarks>
    [Subscription(nameof(Client.SubscribeToSendQueueUpdates), SubscriptionBuffer.All)]
    public static partial IAsyncEnumerable<(string RoomId, RoomSendQueueUpdate Update)> WatchSendQueueUpdatesAsync(
        this Client client,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Runs a sync v2 loop and yields every response.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="settings">The settings of the sync requests.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/loop/*"/>
    /// <para>
    /// The next request starts as soon as a response was received, not when the consumer read it, a slow consumer
    /// builds up responses in memory.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The loop stopped because of an error the SDK doesn't retry. The SDK logs the error.
    /// </exception>
    [Subscription(nameof(Client.SyncV2), SubscriptionBuffer.All, ThrowWhenFinished = true)]
    public static partial IAsyncEnumerable<SyncResponseV2> SyncV2Async(
        this Client client,
        SyncSettingsV2 settings,
        CancellationToken cancellationToken = default
    );
}

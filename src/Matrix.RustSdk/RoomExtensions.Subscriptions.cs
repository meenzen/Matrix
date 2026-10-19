using Matrix.RustSdk.Bindings.RumaEvents;
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
    /// Watches the message-like events of type <paramref name="eventType"/>. Yields each event that arrives with sync,
    /// starting with the next one. Events loaded by pagination, state events of the same type, malformed events and
    /// events that couldn't be decrypted during sync aren't yielded, also when they are decrypted later.
    /// </summary>
    /// <param name="room">The room.</param>
    /// <param name="eventType">
    /// The type of the events, <see cref="RumaEventsMethods.MessageLikeEventTypeFromString"/> creates one for a
    /// custom type. It stays owned by the caller, <see cref="RoomMessageLikeEvent.EventType"/> of the yielded events is
    /// this instance: dispose it after the enumeration ended.
    /// </param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/></remarks>
    public static IAsyncEnumerable<RoomMessageLikeEvent> WatchMessageLikeEventsAsync(
        this Room room,
        MessageLikeEventType eventType,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(eventType);
        return SubscriptionStream.CreateAsync<RoomMessageLikeEvent, TaskHandle>(
            writer => new ValueTask<TaskHandle>(
                room.SubscribeToMessageLikeEvents(eventType, new MessageLikeEventsListener(eventType, writer))
            ),
            SubscriptionBuffer.All,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Watches the state events of type <paramref name="eventType"/>. Yields all of them, one per state key, starting
    /// with the current ones, then after each sync that reported state of that type, which can repeat an unchanged
    /// snapshot.
    /// </summary>
    /// <param name="room">The room.</param>
    /// <param name="eventType">
    /// The type of the events, <see cref="RumaEventsMethods.StateEventTypeFromString"/> creates one for a custom
    /// type. It stays owned by the caller, <see cref="RoomStateEvent.EventType"/> of the yielded events is this
    /// instance: dispose it after the enumeration ended.
    /// </param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/>
    /// <para>
    /// The SDK only stores the state that sync requested. With the sync service (sliding sync) that is a fixed list of
    /// types (the name, topic, avatar, power levels, join rules, members, space parents and children, call members and
    /// a few more), for other types, custom ones included, the snapshot stays empty. Sync v2 stores all state. When the
    /// SDK fails to read the state it skips the snapshot, the first one included.
    /// </para>
    /// </remarks>
    /// <seealso cref="Room.StateEvents"/>
    public static IAsyncEnumerable<RoomStateEvent[]> WatchStateEventsAsync(
        this Room room,
        StateEventType eventType,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(room);
        ArgumentNullException.ThrowIfNull(eventType);
        return SubscriptionStream.CreateAsync<RoomStateEvent[], TaskHandle>(
            writer => new ValueTask<TaskHandle>(
                room.SubscribeToStateEvents(eventType, new StateEventsListener(eventType, writer))
            ),
            SubscriptionBuffer.Latest,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Watches the live sticky events of the room (MSC4354). Yields all of them, at most one per
    /// <see cref="StickyKey"/>, starting with the current ones, then each time an event is added, replaced or expires.
    /// The events are sorted by event type, sticky key and sender.
    /// </summary>
    /// <param name="room">The room.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/>
    /// <para>
    /// Why an event was removed (it expired, was replaced by an empty one or the user left the room) isn't reported.
    /// Leaving the room empties the snapshot without ending the enumeration.
    /// </para>
    /// </remarks>
    /// <seealso cref="Room.StickyEvents"/>
    public static IAsyncEnumerable<StickyEvent[]> WatchStickyEventsAsync(
        this Room room,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(room);
        return SubscriptionStream.CreateAsync<StickyEvent[], TaskHandle>(
            writer => new ValueTask<TaskHandle>(room.SubscribeToStickyEvents(new StickyEventsSnapshotListener(writer))),
            SubscriptionBuffer.Latest,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Replaces the event type of the SDK, which holds a native object for custom types, with the one of the caller.
    /// </summary>
    private sealed class MessageLikeEventsListener(
        MessageLikeEventType eventType,
        SubscriptionWriter<RoomMessageLikeEvent> writer
    ) : RoomMessageLikeEventsListener
    {
        public void OnEvent(RoomMessageLikeEvent @event)
        {
            @event.EventType.Dispose();
            writer.Write(@event with { EventType = eventType });
        }
    }

    /// <summary>
    /// Replaces the event types of the SDK, which hold a native object for custom types, with the one of the caller.
    /// </summary>
    private sealed class StateEventsListener(StateEventType eventType, SubscriptionWriter<RoomStateEvent[]> writer)
        : RoomStateEventsListener
    {
        public void OnUpdate(RoomStateEvent[] events)
        {
            writer.Write(
                Array.ConvertAll(
                    events,
                    stateEvent =>
                    {
                        stateEvent.EventType.Dispose();
                        return stateEvent with { EventType = eventType };
                    }
                )
            );
        }
    }

    /// <summary>
    /// Applies the updates of the SDK to a map of the live events by key and writes the whole map after each of them.
    /// The SDK calls the listener from a single task, one update after the other.
    /// </summary>
    internal sealed class StickyEventsSnapshotListener(SubscriptionWriter<StickyEvent[]> writer) : StickyEventsListener
    {
        private readonly Dictionary<StickyKey, StickyEvent> _events = [];

        public void OnUpdate(StickyEventsUpdate update)
        {
            switch (update)
            {
                case StickyEventsUpdate.Reset reset:
                    _events.Clear();
                    Set(reset.Events);
                    break;
                case StickyEventsUpdate.Changes changes:
                    Set(changes.Added);
                    Set(changes.Updated);
                    foreach (StickyEventRemoval removal in changes.Removed)
                    {
                        _events.Remove(removal.Key);
                    }
                    break;
            }
            writer.Write([
                .. _events
                    .Values.OrderBy(e => e.Key.EventType, StringComparer.Ordinal)
                    .ThenBy(e => e.Key.Key, StringComparer.Ordinal)
                    .ThenBy(e => e.Key.Sender, StringComparer.Ordinal),
            ]);
        }

        private void Set(StickyEvent[] events)
        {
            foreach (StickyEvent stickyEvent in events)
            {
                _events[stickyEvent.Key] = stickyEvent;
            }
        }
    }

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

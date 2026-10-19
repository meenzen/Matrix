using Matrix.RustSdk.Bindings.Ui;

namespace Matrix.RustSdk;

/// <summary>
/// Decides which timeline items are new messages of other users, see
/// <see cref="Bindings.TimelineExtensions.WatchIncomingMessagesAsync(Bindings.Timeline, CancellationToken)"/>.
/// Separate from the enumeration, so it can be tested without the SDK.
/// </summary>
/// <remarks>
/// <para>
/// The origin decides: the timeline loads the cached events with the origin <see cref="EventItemOrigin.Cache"/> when it
/// is created, pagination adds <see cref="EventItemOrigin.Pagination"/>, everything the sync delivers afterwards has
/// <see cref="EventItemOrigin.Sync"/>. The position doesn't matter, the timeline shows only its latest items to a new
/// subscriber and adds the hidden ones in front later, live ones too.
/// </para>
/// <para>
/// The timeline reports events again whenever they change (reactions, receipts, edits, decryption) and re-adds events
/// it already reported after a gappy sync, with the origin sync again. An event counts once, the first time it is seen
/// as a message that arrived by sync. Events that can't be decrypted yet aren't remembered, the decrypted version keeps
/// the origin and counts then. Other events that aren't messages (state events) aren't remembered either, they are
/// checked again whenever they change.
/// </para>
/// <para>
/// The sync that contains the own join also contains the latest events before it, the history of the room. They
/// arrive with the origin sync in the same batch, before the join, and don't count.
/// </para>
/// </remarks>
internal sealed class IncomingMessageFilter(int capacity = IncomingMessageFilter.DefaultCapacity)
{
    /// <summary>
    /// How many event ids are remembered. Events only come back with the origin sync when a later sync contains them
    /// again, those are recent, so forgetting the oldest ids keeps the memory bounded without counting events twice.
    /// </summary>
    internal const int DefaultCapacity = 10_000;

    private readonly HashSet<string> _known = [];
    private readonly Queue<string> _order = new();

    /// <summary>
    /// Returns the indexes of the new messages of other users in <paramref name="items"/>, the items of one batch of
    /// diffs in order.
    /// </summary>
    public List<int> Filter(IReadOnlyList<Item> items)
    {
        List<int> incoming = [];
        for (int i = 0; i < items.Count; i++)
        {
            Item item = items[i];
            if (item.IsOwnJoin)
            {
                // everything before the join is history, including events that may be decrypted later
                for (int j = 0; j < i; j++)
                {
                    if (items[j].EventId is { } eventId)
                    {
                        Remember(eventId);
                    }
                }
                incoming.Clear();
            }

            if (IsIncoming(item))
            {
                incoming.Add(i);
            }
        }
        return incoming;
    }

    private bool IsIncoming(Item item)
    {
        // local echoes and virtual items have no event id, the remote echo of an own message is skipped below
        if (item.EventId is not { } eventId || _known.Contains(eventId))
        {
            return false;
        }

        // own messages and events loaded from the cache or by pagination never count, even when they are re-added
        // with the origin sync later
        if (item.IsOwn || item.Origin != EventItemOrigin.Sync)
        {
            Remember(eventId);
            return false;
        }

        // everything else may still become a message, events that couldn't be decrypted yet
        if (!item.IsMessage)
        {
            return false;
        }

        Remember(eventId);
        return true;
    }

    private void Remember(string eventId)
    {
        if (!_known.Add(eventId))
        {
            return;
        }

        _order.Enqueue(eventId);
        if (_order.Count > capacity)
        {
            _known.Remove(_order.Dequeue());
        }
    }

    /// <summary>
    /// What the filter needs to know about a timeline item.
    /// </summary>
    /// <param name="EventId">The event id, <see langword="null"/> for local echoes and virtual items.</param>
    /// <param name="IsOwn">Whether the own account sent the event.</param>
    /// <param name="Origin">Where the timeline got the event from.</param>
    /// <param name="IsMessage">Whether the event is a message (<c>m.room.message</c>).</param>
    /// <param name="IsOwnJoin">Whether the event is the own account joining the room.</param>
    internal readonly record struct Item(
        string? EventId,
        bool IsOwn,
        EventItemOrigin? Origin,
        bool IsMessage,
        bool IsOwnJoin = false
    );
}

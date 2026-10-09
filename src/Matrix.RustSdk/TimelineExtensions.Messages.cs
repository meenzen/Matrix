using System.Runtime.CompilerServices;
using Matrix.RustSdk.Subscriptions;

namespace Matrix.RustSdk.Bindings;

public static partial class TimelineExtensions
{
    /// <summary>
    /// Sends a text message (<c>m.text</c>), see
    /// <see cref="RoomMessageEventContentWithoutRelationExtensions.extension(RoomMessageEventContentWithoutRelation).Text"/>.
    /// </summary>
    /// <param name="timeline">The timeline of the room.</param>
    /// <param name="body">The plain text body.</param>
    /// <include file="Messages.xml" path="docs/send/*"/>
    public static Task SendTextAsync(this Timeline timeline, string body, string? inReplyTo = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ThrowIfNoEventId(inReplyTo);
        return SendAsync(timeline, RoomMessageEventContentWithoutRelation.Text(body), inReplyTo);
    }

    /// <summary>
    /// Sends a text message (<c>m.text</c>) from markdown, see
    /// <see cref="RoomMessageEventContentWithoutRelationExtensions.extension(RoomMessageEventContentWithoutRelation).Markdown"/>.
    /// </summary>
    /// <param name="timeline">The timeline of the room.</param>
    /// <param name="markdown">The markdown.</param>
    /// <include file="Messages.xml" path="docs/send/*"/>
    public static Task SendMarkdownAsync(this Timeline timeline, string markdown, string? inReplyTo = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ThrowIfNoEventId(inReplyTo);
        return SendAsync(timeline, RoomMessageEventContentWithoutRelation.Markdown(markdown), inReplyTo);
    }

    /// <summary>
    /// Sends a notice (<c>m.notice</c>), the kind of message bots send, see
    /// <see cref="RoomMessageEventContentWithoutRelationExtensions.extension(RoomMessageEventContentWithoutRelation).Notice"/>.
    /// </summary>
    /// <param name="timeline">The timeline of the room.</param>
    /// <param name="body">The plain text body.</param>
    /// <include file="Messages.xml" path="docs/send/*"/>
    public static Task SendNoticeAsync(this Timeline timeline, string body, string? inReplyTo = null)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        ThrowIfNoEventId(inReplyTo);
        return SendAsync(timeline, RoomMessageEventContentWithoutRelation.Notice(body), inReplyTo);
    }

    /// <summary>
    /// Watches the timeline for new messages of other users. Yields every message (<c>m.room.message</c>) that arrived
    /// by sync since the timeline was created, once. The yielded items always have an
    /// <see cref="EventTimelineItemExtensions.extension(EventTimelineItem).EventId"/>.
    /// <see cref="EventTimelineItemExtensions.extension(EventTimelineItem).Message"/> has the content,
    /// <see cref="EventTimelineItemExtensions.TryGetText"/> the body of text messages.
    /// </summary>
    /// <param name="timeline">The live timeline of the room (<see cref="Room.Timeline"/>).</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/disposable/*"/>
    /// <para>
    /// Not yielded: messages the timeline loaded from the cache when it was created or later by pagination, the history
    /// the sync delivers together with the own join, messages of the own account (from any device), and edits,
    /// reactions or redactions of messages, which change a message instead of adding one. Messages that arrive between creating the timeline and starting the enumeration are
    /// yielded, so no message gets lost while the subscription starts. Enumerate a timeline once: a second enumeration
    /// of the same timeline yields the messages the first one yielded again. Messages that couldn't be decrypted are
    /// yielded once they are decrypted, which can be long after they were sent. Messages in threads are yielded too,
    /// <see cref="EventTimelineItemExtensions.extension(EventTimelineItem).ThreadRootEventId"/> tells the thread,
    /// reply to them to answer in the thread.
    /// </para>
    /// <para>
    /// Every kind of message is yielded, notices (<see cref="MessageType.Notice"/>) of other bots too. By convention
    /// bots only answer text messages (<see cref="EventTimelineItemExtensions.TryGetText"/>), answering notices lets
    /// bots answer each other in a loop.
    /// </para>
    /// <para>
    /// Only messages the sync delivers are yielded. When more events arrive between two syncs than the sync returns
    /// per room, the timeline gets a gap, the SDK only loads the missing events by pagination and those aren't
    /// yielded. Sliding sync (<see cref="SyncService"/>) returns only the latest event of every room by default, bots
    /// raise it with <see cref="SyncServiceBuilder.WithRoomListTimelineLimit"/>. Messages that arrive while the SDK
    /// reloads the timeline from its cache, because the client fell behind, look like they were loaded from the cache
    /// and aren't yielded either. The other way round, the SDK adds the events of a sync to its cache in the
    /// background: a timeline created right after a sync can miss them when it loads the cache and gets them as new
    /// events, so the first sync after a start can yield recent messages, with a store that is kept across starts
    /// those that arrived while the client was offline. Bots that don't want those compare
    /// <see cref="EventTimelineItemExtensions.extension(EventTimelineItem).SentAt"/> with the time they started.
    /// </para>
    /// </remarks>
    public static IAsyncEnumerable<EventTimelineItem> WatchIncomingMessagesAsync(
        this Timeline timeline,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(timeline);
        return WatchIncomingMessagesAsync(timeline.WatchItemDiffsAsync(cancellationToken), cancellationToken);
    }

    private static async IAsyncEnumerable<EventTimelineItem> WatchIncomingMessagesAsync(
        IAsyncEnumerable<VectorDiff<TimelineItem>[]> diffs,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        IncomingMessageFilter filter = new();
        await foreach (
            VectorDiff<TimelineItem>[] batch in diffs.WithCancellation(cancellationToken).ConfigureAwait(false)
        )
        {
            List<EventTimelineItem> messages = Incoming(filter, batch);
            int yielded = 0;
            try
            {
                while (yielded < messages.Count)
                {
                    // belongs to the consumer once it is yielded
                    EventTimelineItem message = messages[yielded++];
                    yield return message;
                }
            }
            finally
            {
                // the consumer stopped in the middle of the batch
                SubscriptionStream.Dispose(messages[yielded..]);
            }
        }
    }

    /// <summary>
    /// Returns the new messages of other users in <paramref name="batch"/>, disposes the batch and everything else.
    /// </summary>
    private static List<EventTimelineItem> Incoming(IncomingMessageFilter filter, VectorDiff<TimelineItem>[] batch)
    {
        // AsEvent returns copies, they live on when the diffs are disposed
        List<EventTimelineItem?> events = [];
        try
        {
            foreach (VectorDiff<TimelineItem> diff in batch)
            {
                foreach (TimelineItem item in diff.GetValues())
                {
                    // virtual items like day dividers aren't events
                    if (item.AsEvent() is { } eventItem)
                    {
                        events.Add(eventItem);
                    }
                }
            }

            List<EventTimelineItem> messages = [];
            foreach (int index in filter.Filter([.. events.Select(eventItem => ToFilterItem(eventItem!))]))
            {
                messages.Add(events[index]!);
                events[index] = null;
            }
            return messages;
        }
        finally
        {
            SubscriptionStream.Dispose(events);
            SubscriptionStream.Dispose(batch);
        }
    }

    private static IncomingMessageFilter.Item ToFilterItem(EventTimelineItem item) =>
        new(
            item.EventId,
            item.IsOwn,
            item.Origin,
            item.Message is not null,
            IsOwnJoin: item.Content
                is TimelineItemContent.RoomMembership
                {
                    Change: MembershipChange.Joined or MembershipChange.InvitationAccepted,
                } membership
                && membership.UserId == item.Sender
                && item.IsOwn
        );

    private static void ThrowIfNoEventId(string? inReplyTo)
    {
        // event ids start with $, catches html passed where the reply is expected (Text(body, html))
        if (inReplyTo is not null && !inReplyTo.StartsWith('$'))
        {
            throw new ArgumentException($"'{inReplyTo}' isn't an event id.", nameof(inReplyTo));
        }
    }

    private static async Task SendAsync(
        Timeline timeline,
        RoomMessageEventContentWithoutRelation content,
        string? inReplyTo
    )
    {
        using (content)
        {
            // disposing the handle doesn't abort sending, Timeline.Send returns it for those who need it
            using SendHandle _ = inReplyTo is null
                ? await timeline.Send(content).ConfigureAwait(false)
                : await timeline.SendReply(content, inReplyTo).ConfigureAwait(false);
        }
    }
}

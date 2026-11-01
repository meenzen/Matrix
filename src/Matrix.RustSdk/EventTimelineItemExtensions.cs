using System.Diagnostics.CodeAnalysis;

namespace Matrix.RustSdk.Bindings;

/// <summary>
/// Helpers for <see cref="EventTimelineItem"/>.
/// </summary>
public static class EventTimelineItemExtensions
{
    // DateTimeOffset.FromUnixTimeMilliseconds throws beyond the year 9999, the timestamps of remote events are claimed
    // by the sending server
    private static readonly ulong MaxTimestamp = (ulong)DateTimeOffset.MaxValue.ToUnixTimeMilliseconds();

    extension(EventTimelineItem item)
    {
        /// <summary>
        /// The id of the event, <see langword="null"/> for local echoes the server didn't confirm yet, those only have
        /// a transaction id.
        /// </summary>
        public string? EventId => (item.EventOrTransactionId as EventOrTransactionId.EventId)?.EventIdValue;

        /// <summary>
        /// The name to show for the sender: the display name, followed by the user id when another member of the room
        /// uses the same display name (<c>Alice (@alice:example.org)</c>). The user id while the profile isn't loaded
        /// or when the sender has no display name.
        /// </summary>
        public string SenderDisplayName =>
            item.SenderProfile switch
            {
                ProfileDetails.Ready { DisplayName: { } name, DisplayNameAmbiguous: true }
                    when !string.IsNullOrWhiteSpace(name) => $"{name} ({item.Sender})",
                ProfileDetails.Ready { DisplayName: { } name } when !string.IsNullOrWhiteSpace(name) => name,
                _ => item.Sender,
            };

        /// <summary>
        /// <see cref="EventTimelineItem.Timestamp"/> as a <see cref="DateTimeOffset"/> in UTC. For remote events this
        /// is the time the sending server claims (<c>origin_server_ts</c>), don't rely on it for ordering. For local
        /// echoes it is the time the echo was created, the event may not be sent yet.
        /// </summary>
        public DateTimeOffset SentAt =>
            DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Min(item.Timestamp, MaxTimestamp));

        /// <summary>
        /// The content of the message, <see langword="null"/> if the event isn't a message (<c>m.room.message</c>):
        /// state events, stickers, polls, redacted events or events that couldn't be decrypted yet.
        /// <see cref="MessageContent.MsgType"/> tells the kind of message, <see cref="MessageContent.Body"/> is its
        /// plain text for every kind (the file name of an image, the action of an emote).
        /// </summary>
        /// <remarks>
        /// The content belongs to the item, don't dispose it. It is only valid until the item is disposed: copy what
        /// you need instead of keeping it.
        /// </remarks>
        public MessageContent? Message =>
            item.Content is TimelineItemContent.MsgLike { Content.Kind: MsgLikeKind.Message message }
                ? message.Content
                : null;

        /// <summary>
        /// The id of the thread root if the event is part of a thread. To answer in the thread, reply to this event:
        /// pass its <see cref="extension(EventTimelineItem).EventId"/> as <c>inReplyTo</c> of
        /// <see cref="TimelineExtensions.SendTextAsync"/>, not the thread root.
        /// </summary>
        public string? ThreadRootEventId => (item.Content as TimelineItemContent.MsgLike)?.Content.ThreadRoot;

        /// <summary>
        /// The id of the event this event replies to, <see langword="null"/> if it isn't a reply. On the live timeline,
        /// messages in threads usually report the thread fallback here (the previous message of the thread for clients
        /// without threads), even when the sender didn't reply: check
        /// <see cref="extension(EventTimelineItem).ThreadRootEventId"/> first.
        /// </summary>
        public string? InReplyToEventId => (item.Content as TimelineItemContent.MsgLike)?.Content.InReplyTo?.EventId();

        /// <summary>
        /// Gets the plain text body of a text message (<c>m.text</c>). Returns <see langword="false"/> for every other
        /// kind of message (notices, emotes, media, see <see cref="extension(EventTimelineItem).Message"/>) and for
        /// events that aren't messages. Bots answer only text messages, by convention they don't answer notices.
        /// </summary>
        /// <param name="body">The plain text body, <see langword="null"/> if the event isn't a text message.</param>
        /// <returns>Whether the event is a text message.</returns>
        public bool TryGetText([NotNullWhen(true)] out string? body)
        {
            body = (item.Message?.MsgType as MessageType.Text)?.Content.Body;
            return body is not null;
        }
    }
}

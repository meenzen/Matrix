using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk.Examples.TuiClient.Chat;

public enum EntryKind
{
    Message,
    Emote,
    Notice,
    Media,
    Encrypted,
    Redacted,
    Event,
    DayDivider,
    ReadMarker,
}

public enum SendStatus
{
    Sent,
    Sending,
    Failed,
}

/// <summary>
/// The message a reply refers to. <see cref="Body"/> is null while it is loading.
/// </summary>
public sealed record ReplyPreview(string EventId, string? SenderName, string? Body);

public sealed record ReactionSummary(string Key, int Count, bool IsOwn);

/// <summary>
/// An attachment. <see cref="SourceJson"/> is the serialized <see cref="MediaSource"/>: the source itself is a native
/// object that is gone when the timeline item is disposed.
/// </summary>
public sealed record MediaAttachment(string Kind, string Filename, string? MimeType, ulong? Size, string SourceJson);

/// <summary>
/// What the client shows of a timeline item, made of managed values only so it can be kept after the item is
/// disposed. <see cref="Key"/> identifies the item across updates (the SDK's unique id), <see cref="Id"/> is what the
/// timeline needs to edit, redact or react to an event, also before it was sent.
/// </summary>
public sealed record TimelineEntry(string Key, EntryKind Kind, string Body)
{
    public DateTimeOffset? Time { get; init; }
    public string? SenderId { get; init; }
    public string? SenderName { get; init; }
    public EventOrTransactionId? Id { get; init; }
    public string? EventId { get; init; }
    public bool IsOwn { get; init; }
    public bool IsEditable { get; init; }
    public bool IsEdited { get; init; }
    public bool CanReply { get; init; }
    public bool MentionsMe { get; init; }
    public SendStatus Status { get; init; }
    public ReplyPreview? ReplyTo { get; init; }
    public MediaAttachment? Media { get; init; }
    public IReadOnlyList<ReactionSummary> Reactions { get; init; } = [];

    /// <summary>
    /// The users whose read receipt is on this event, without the own user.
    /// </summary>
    public IReadOnlyList<string> ReadBy { get; init; } = [];

    /// <summary>
    /// Whether the entry is a message that can be selected for actions, as opposed to dividers.
    /// </summary>
    public bool IsEvent => Kind is not (EntryKind.DayDivider or EntryKind.ReadMarker);
}

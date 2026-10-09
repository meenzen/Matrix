using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk.Examples.TuiClient;

/// <summary>
/// The live timeline of an opened room, rendered as one line per event. Like <see cref="MatrixSession"/> this class
/// doesn't know anything about the UI, <c>onChanged</c> is called on a thread pool thread.
/// </summary>
public sealed class RoomTimeline : IAsyncDisposable
{
    private const ushort HistoryEvents = 50;

    private readonly Room _room;
    private readonly Timeline _timeline;

    // one line per timeline item, null for items that aren't shown
    private readonly LiveList<string?> _lines;

    private RoomTimeline(Room room, Timeline timeline, LiveList<string?> lines)
    {
        _room = room;
        _timeline = timeline;
        _lines = lines;
    }

    /// <summary>
    /// The lines of the shown events, oldest first.
    /// </summary>
    public IReadOnlyList<string> Lines => [.. _lines.OfType<string>()];

    /// <summary>
    /// Opens the timeline of <paramref name="room"/>, joining it first if the user was invited. The timeline owns the
    /// room from now on. <paramref name="onChanged"/> is called whenever <see cref="Lines"/> changes.
    /// </summary>
    public static async Task<RoomTimeline> OpenAsync(Room room, System.Action onChanged)
    {
        Timeline timeline;
        try
        {
            if (room.Membership() == Membership.Invited)
            {
                await room.Join();
            }
            timeline = await room.Timeline();
        }
        catch
        {
            room.Dispose();
            throw;
        }

        // the diffs start with the current items, every timeline item is formatted when it arrives and disposed
        // right after
        LiveList<string?> lines = timeline
            .WatchItemDiffsAsync()
            .ToLiveList(Format, synchronizationContext: MatrixSession.ThreadPool);
        lines.Changed += (_, _) => onChanged();
        RoomTimeline roomTimeline = new(room, timeline, lines);
        try
        {
            // the list may have changed before the handler was attached
            onChanged();
            // the timeline only contains what the sync loaded so far, fetch some history and the members for the
            // display names of the senders
            await timeline.PaginateBackwards(HistoryEvents);
            await timeline.FetchMembers();
            return roomTimeline;
        }
        catch
        {
            await roomTimeline.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Sends a text message, markdown is converted to HTML. The message shows up in the timeline right away as a local
    /// echo and is updated once the server confirms it.
    /// </summary>
    public Task SendAsync(string text) => _timeline.SendMarkdownAsync(text);

    public async ValueTask DisposeAsync()
    {
        // ends the subscription
        await _lines.DisposeAsync();
        _timeline.Dispose();
        _room.Dispose();
    }

    /// <summary>
    /// Formats messages and membership changes, everything else (state events, reactions, day dividers, ...) is
    /// skipped to keep the example short.
    /// </summary>
    private static string? Format(TimelineItem item)
    {
        using EventTimelineItem? eventItem = item.AsEvent();
        if (eventItem is null)
        {
            // virtual items like day dividers and the read marker
            return null;
        }

        string sender = eventItem.SenderDisplayName;
        string time = eventItem
            .SentAt.ToLocalTime()
            .ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        // the plain text of every kind of message, emotes and media aren't rendered differently
        string? text = eventItem.Message is { } message
            ? $"<{sender}> {message.Body.ReplaceLineEndings(" ")}"
            : eventItem.Content switch
            {
                TimelineItemContent.MsgLike { Content.Kind: MsgLikeKind.UnableToDecrypt } =>
                    $"<{sender}> (unable to decrypt)",
                TimelineItemContent.MsgLike { Content.Kind: MsgLikeKind.Redacted } => $"<{sender}> (deleted)",
                TimelineItemContent.RoomMembership membership =>
                    $"* {membership.UserDisplayName ?? membership.UserId}: {membership.Change}",
                _ => null,
            };
        return text is null ? null : $"{time} {text}";
    }
}

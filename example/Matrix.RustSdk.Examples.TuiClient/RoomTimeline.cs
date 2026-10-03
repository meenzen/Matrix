using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk.Examples.TuiClient;

/// <summary>
/// The live timeline of an opened room, rendered as one line per event. Like <see cref="MatrixSession"/> this class
/// doesn't know anything about the UI, <c>onChanged</c> is called on a thread of the SDK.
/// </summary>
public sealed class RoomTimeline : IDisposable
{
    private const ushort HistoryEvents = 50;

    private readonly Timeline _timeline;
    private readonly TaskHandle _listenerHandle;

    private RoomTimeline(Timeline timeline, TaskHandle listenerHandle)
    {
        _timeline = timeline;
        _listenerHandle = listenerHandle;
    }

    /// <summary>
    /// Opens the timeline of <paramref name="room"/>, joining it first if the user was invited.
    /// <paramref name="onChanged"/> is called with all lines whenever the timeline changes.
    /// </summary>
    public static async Task<RoomTimeline> OpenAsync(Room room, Action<IReadOnlyList<string>> onChanged)
    {
        if (room.Membership() == Membership.Invited)
        {
            await room.Join();
        }

        Timeline timeline = await room.Timeline();
        // the listener gets the current items as a reset first, then every change as a list of diffs
        TaskHandle listenerHandle = await timeline.AddListener(new TimelineObserver(onChanged));
        // the timeline only contains what the sync loaded so far, fetch some history and the members for the
        // display names of the senders
        await timeline.PaginateBackwards(HistoryEvents);
        await timeline.FetchMembers();
        return new RoomTimeline(timeline, listenerHandle);
    }

    /// <summary>
    /// Sends a text message, markdown is converted to HTML. The message shows up in the timeline right away as a local
    /// echo and is updated once the server confirms it.
    /// </summary>
    public async Task SendAsync(string text)
    {
        using RoomMessageEventContentWithoutRelation content = MatrixSdkFfiMethods.MessageEventContentFromMarkdown(
            text
        );
        using SendHandle sendHandle = await _timeline.Send(content);
    }

    public void Dispose()
    {
        _listenerHandle.Cancel();
        _listenerHandle.Dispose();
        _timeline.Dispose();
    }

    /// <summary>
    /// Keeps one line per timeline item (null for items that aren't shown) and applies the diffs of the SDK to it.
    /// </summary>
    private sealed class TimelineObserver(Action<IReadOnlyList<string>> onChanged) : TimelineListener
    {
        private readonly Lock _lock = new();
        private readonly List<string?> _lines = [];

        public void OnUpdate(TimelineDiff[] diff)
        {
            string[] snapshot;
            lock (_lock)
            {
                foreach (TimelineDiff update in diff)
                {
                    Apply(update);
                    // the lines are copied, the native timeline items aren't needed anymore
                    update.Dispose();
                }
                snapshot = [.. _lines.OfType<string>()];
            }
            onChanged(snapshot);
        }

        private void Apply(TimelineDiff update)
        {
            switch (update)
            {
                case TimelineDiff.Append append:
                    _lines.AddRange(append.Values.Select(Format));
                    break;
                case TimelineDiff.Clear:
                    _lines.Clear();
                    break;
                case TimelineDiff.PushFront pushFront:
                    _lines.Insert(0, Format(pushFront.Value));
                    break;
                case TimelineDiff.PushBack pushBack:
                    _lines.Add(Format(pushBack.Value));
                    break;
                case TimelineDiff.PopFront:
                    _lines.RemoveAt(0);
                    break;
                case TimelineDiff.PopBack:
                    _lines.RemoveAt(_lines.Count - 1);
                    break;
                case TimelineDiff.Insert insert:
                    _lines.Insert((int)insert.Index, Format(insert.Value));
                    break;
                case TimelineDiff.Set set:
                    _lines[(int)set.Index] = Format(set.Value);
                    break;
                case TimelineDiff.Remove remove:
                    _lines.RemoveAt((int)remove.Index);
                    break;
                case TimelineDiff.Truncate truncate:
                    _lines.RemoveRange((int)truncate.Length, _lines.Count - (int)truncate.Length);
                    break;
                case TimelineDiff.Reset reset:
                    _lines.Clear();
                    _lines.AddRange(reset.Values.Select(Format));
                    break;
            }
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

            string sender = eventItem.SenderProfile is ProfileDetails.Ready { DisplayName: { } displayName }
                ? displayName
                : eventItem.Sender;
            string time = DateTimeOffset
                .FromUnixTimeMilliseconds((long)eventItem.Timestamp)
                .ToLocalTime()
                .ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

            string? text = eventItem.Content switch
            {
                TimelineItemContent.MsgLike { Content.Kind: MsgLikeKind.Message message } =>
                    $"<{sender}> {message.Content.Body.ReplaceLineEndings(" ")}",
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
}

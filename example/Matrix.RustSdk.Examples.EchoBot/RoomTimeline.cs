using System.Threading.Channels;
using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Bindings.Ui;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Matrix.RustSdk.Examples.EchoBot;

/// <summary>
/// A text message of another user, received while the bot is running.
/// </summary>
public sealed record ReceivedMessage(Timeline Timeline, string RoomId, string Sender, string Body);

/// <summary>
/// The live timeline of a joined room. It passes new text messages of other users to a channel.
/// </summary>
public sealed partial class RoomTimeline : TimelineListener, IDisposable
{
    private readonly string _roomId;
    private readonly Timeline _timeline;
    private readonly ChannelWriter<ReceivedMessage> _messages;
    private readonly ILogger _logger;

    // the timeline reports events again when they change (read receipts, reactions, edits), handle each one once
    private readonly HashSet<string> _seenEvents = [];
    private readonly Lock _lock = new();
    private TaskHandle? _listener;

    private RoomTimeline(string roomId, Timeline timeline, ChannelWriter<ReceivedMessage> messages, ILogger logger)
    {
        _roomId = roomId;
        _timeline = timeline;
        _messages = messages;
        _logger = logger;
    }

    public static async Task<RoomTimeline> CreateAsync(
        Room room,
        ChannelWriter<ReceivedMessage> messages,
        ILogger logger
    )
    {
        Timeline timeline = await room.Timeline();
        RoomTimeline roomTimeline = new(room.Id(), timeline, messages, logger);
        roomTimeline._listener = await timeline.AddListener(roomTimeline);
        return roomTimeline;
    }

    /// <summary>
    /// Called by the SDK on one of its threads whenever the timeline changes.
    /// </summary>
    public void OnUpdate(TimelineDiff[] diff)
    {
        try
        {
            lock (_lock)
            {
                foreach (TimelineDiff change in diff)
                {
                    using (change)
                    {
                        Handle(change);
                    }
                }
            }
        }
        catch (Exception e)
        {
            // exceptions must not escape into the SDK
            LogUpdateFailed(e, _roomId);
        }
    }

    private void Handle(TimelineDiff diff)
    {
        switch (diff)
        {
            // the initial items are history, don't answer them
            case TimelineDiff.Reset reset:
                foreach (TimelineItem item in reset.Values)
                {
                    Process(item, isHistory: true);
                }
                break;
            case TimelineDiff.Append append:
                foreach (TimelineItem item in append.Values)
                {
                    Process(item, isHistory: false);
                }
                break;
            case TimelineDiff.PushBack pushBack:
                Process(pushBack.Value, isHistory: false);
                break;
            case TimelineDiff.Insert insert:
                Process(insert.Value, isHistory: false);
                break;
            case TimelineDiff.Set set:
                Process(set.Value, isHistory: false);
                break;
        }
    }

    private void Process(TimelineItem item, bool isHistory)
    {
        using EventTimelineItem? timelineEvent = item.AsEvent();

        // local echoes of messages that are still being sent have no event id yet
        if (timelineEvent?.EventOrTransactionId is not EventOrTransactionId.EventId { EventIdValue: var eventId })
        {
            return;
        }
        if (!_seenEvents.Add(eventId) || isHistory)
        {
            return;
        }

        // never answer our own messages, the bot would talk to itself forever, and only answer new events from the
        // sync, not ones loaded from the cache or by back pagination
        if (timelineEvent.IsOwn || timelineEvent.Origin != EventItemOrigin.Sync)
        {
            return;
        }

        if (
            timelineEvent.Content is TimelineItemContent.MsgLike
            {
                Content.Kind: MsgLikeKind.Message { Content.MsgType: MessageType.Text text }
            }
        )
        {
            _messages.TryWrite(new ReceivedMessage(_timeline, _roomId, timelineEvent.Sender, text.Content.Body));
        }
    }

    public void Dispose()
    {
        _listener?.Cancel();
        _listener?.Dispose();
        _timeline.Dispose();
    }

    [LoggerMessage(LogLevel.Error, "Handling a timeline update of {RoomId} failed")]
    private partial void LogUpdateFailed(Exception exception, string roomId);
}

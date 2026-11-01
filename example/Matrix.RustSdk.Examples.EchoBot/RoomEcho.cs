using Matrix.RustSdk.Bindings;
using Microsoft.Extensions.Logging;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Matrix.RustSdk.Examples.EchoBot;

/// <summary>
/// Echoes the text messages of other users in a joined room, as notices replying to them, so messages in threads are
/// answered in the thread.
/// </summary>
public sealed partial class RoomEcho : IAsyncDisposable
{
    private readonly string _roomId;
    private readonly Timeline _timeline;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _echoing;

    private RoomEcho(string roomId, Timeline timeline, ILogger logger)
    {
        _roomId = roomId;
        _timeline = timeline;
        _logger = logger;
        _echoing = EchoAsync(_stopping.Token);
    }

    /// <summary>
    /// Whether the echo loop runs, it stops when it fails and when the echo is disposed.
    /// </summary>
    public bool IsEchoing => !_echoing.IsCompleted;

    /// <summary>
    /// Opens the timeline of <paramref name="room"/> and starts echoing. Messages that arrive from now on are
    /// answered, the ones before are history.
    /// </summary>
    public static async Task<RoomEcho> StartAsync(Room room, ILogger logger)
    {
        Timeline timeline = await room.Timeline();
        return new RoomEcho(room.Id(), timeline, logger);
    }

    private async Task EchoAsync(CancellationToken cancellationToken)
    {
        try
        {
            // new messages of other users received by sync, each once: no history, no own messages
            await foreach (EventTimelineItem message in _timeline.WatchIncomingMessagesAsync(cancellationToken))
            {
                using (message)
                {
                    // only text messages, answering the notices of other bots would let them talk forever
                    if (message.TryGetText(out string? body))
                    {
                        LogEchoing(message.Sender, _roomId);
                        await ReplyAsync(body, message.EventId!);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // the bot left the room or stops
        }
        catch (Exception e) when (!cancellationToken.IsCancellationRequested)
        {
            // the loop failed on its own while the bot is in the room, it runs in the background: log right away
            // instead of failing DisposeAsync much later
            LogEchoingStopped(e, _roomId);
        }
    }

    private async Task ReplyAsync(string body, string eventId)
    {
        try
        {
            // queues the reply, the SDK sends it in the background. Replies stay in the thread and mention the sender,
            // notices are what bots send, other bots don't answer them
            await _timeline.SendNoticeAsync(body, inReplyTo: eventId);
        }
        catch (ClientException e)
        {
            LogEchoFailed(e, _roomId);
        }
    }

    /// <summary>
    /// Stops echoing, messages that are being echoed are still queued.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        using (_timeline)
        using (_stopping)
        {
            await _stopping.CancelAsync();
            // the echo loop was started by this object and doesn't need the caller's context
#pragma warning disable VSTHRD003
            await _echoing;
#pragma warning restore VSTHRD003
        }
    }

    [LoggerMessage(LogLevel.Information, "Echoing a message of {Sender} in {RoomId}")]
    private partial void LogEchoing(string sender, string roomId);

    [LoggerMessage(LogLevel.Warning, "Echoing a message in {RoomId} failed")]
    private partial void LogEchoFailed(Exception exception, string roomId);

    [LoggerMessage(LogLevel.Error, "Echoing in {RoomId} stopped")]
    private partial void LogEchoingStopped(Exception exception, string roomId);
}

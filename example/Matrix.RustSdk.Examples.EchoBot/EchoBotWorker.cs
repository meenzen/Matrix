using System.Threading.Channels;
using Matrix.RustSdk.Bindings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Matrix.RustSdk.Examples.EchoBot;

/// <summary>
/// Logs in, joins every room the bot is invited to and sends text messages of other users back into the room.
/// </summary>
public sealed partial class EchoBotWorker(IOptions<EchoBotOptions> options, ILogger<EchoBotWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    // the timelines of the joined rooms, only used by the sync loop
    private readonly Dictionary<string, RoomTimeline> _rooms = [];

    // messages received by the timeline listeners, the echo loop sends them back
    private readonly Channel<ReceivedMessage> _messages = Channel.CreateUnbounded<ReceivedMessage>(
        new UnboundedChannelOptions { SingleReader = true }
    );

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        EchoBotOptions settings = options.Value;

        // the bindings don't support cancellation, WaitAsync stops waiting for the SDK when the host shuts down
        using Client client = await new ClientBuilder()
            .ServerNameOrHomeserverUrl(settings.Homeserver)
            .InMemoryStore()
            .Build()
            .WaitAsync(stoppingToken);
        await client
            .Login(settings.Username, settings.Password, settings.DeviceName, deviceId: null)
            .WaitAsync(stoppingToken);
        LogLoggedIn(client.UserId());

        Task echoing = EchoAsync();
        try
        {
            await SyncAsync(client, stoppingToken);
        }
        finally
        {
            // lets the echo loop send the remaining messages and stop
            _messages.Writer.Complete();
            await echoing;

            foreach (RoomTimeline room in _rooms.Values)
            {
                room.Dispose();
            }
            _rooms.Clear();
            LogStopped();
        }
    }

    private async Task SyncAsync(Client client, CancellationToken cancellationToken)
    {
        // a classic /sync long poll, the SDK remembers the token of the previous response
        SyncSettingsV2 settings = new(TimeoutMs: 30_000);
        while (!cancellationToken.IsCancellationRequested)
        {
            SyncResponseV2 response;
            try
            {
                response = await client.SyncOnceV2(settings).WaitAsync(cancellationToken);
            }
            catch (ClientException e)
            {
                LogSyncFailed(e, RetryDelay);
                await Task.Delay(RetryDelay, cancellationToken);
                continue;
            }

            foreach (string roomId in response.Rooms.Invited)
            {
                await JoinAsync(client, roomId).WaitAsync(cancellationToken);
            }
            foreach (string roomId in response.Rooms.Joined)
            {
                await ListenAsync(client, roomId).WaitAsync(cancellationToken);
            }
            foreach (string roomId in response.Rooms.Left)
            {
                if (_rooms.Remove(roomId, out RoomTimeline? room))
                {
                    room.Dispose();
                    LogLeft(roomId);
                }
            }
        }
    }

    private async Task JoinAsync(Client client, string roomId)
    {
        using Room? room = client.GetRoom(roomId);
        if (room is null || room.Membership() != Membership.Invited)
        {
            return;
        }

        try
        {
            await room.Join();
            LogJoined(roomId);
        }
        catch (ClientException e)
        {
            LogJoinFailed(e, roomId);
            return;
        }

        // listen right away, otherwise messages sent before the next sync would count as history
        await ListenAsync(client, roomId);
    }

    private async Task ListenAsync(Client client, string roomId)
    {
        if (_rooms.ContainsKey(roomId))
        {
            return;
        }

        using Room? room = client.GetRoom(roomId);
        if (room is null || room.Membership() != Membership.Joined)
        {
            return;
        }

        try
        {
            _rooms[roomId] = await RoomTimeline.CreateAsync(room, _messages.Writer, logger);
        }
        catch (ClientException e)
        {
            LogTimelineFailed(e, roomId);
        }
    }

    private async Task EchoAsync()
    {
        await foreach (ReceivedMessage message in _messages.Reader.ReadAllAsync())
        {
            LogEchoing(message.Sender, message.RoomId);
            try
            {
                using RoomMessageEventContentWithoutRelation content = MatrixSdkFfiMethods.MessageEventContentNew(
                    new MessageType.Text(new TextMessageContent(message.Body, Formatted: null))
                );
                // queues the message, the SDK sends it in the background
                using SendHandle _ = await message.Timeline.Send(content);
            }
            // the timeline is disposed when the bot left the room in the meantime
            catch (Exception e) when (e is ClientException or ObjectDisposedException)
            {
                LogEchoFailed(e, message.RoomId);
            }
        }
    }

    [LoggerMessage(LogLevel.Information, "Logged in as {UserId}")]
    private partial void LogLoggedIn(string userId);

    [LoggerMessage(LogLevel.Information, "Stopped")]
    private partial void LogStopped();

    [LoggerMessage(LogLevel.Warning, "Sync failed, retrying in {Delay}")]
    private partial void LogSyncFailed(Exception exception, TimeSpan delay);

    [LoggerMessage(LogLevel.Information, "Joined {RoomId}")]
    private partial void LogJoined(string roomId);

    [LoggerMessage(LogLevel.Warning, "Joining {RoomId} failed")]
    private partial void LogJoinFailed(Exception exception, string roomId);

    [LoggerMessage(LogLevel.Information, "Left {RoomId}")]
    private partial void LogLeft(string roomId);

    [LoggerMessage(LogLevel.Warning, "Opening the timeline of {RoomId} failed")]
    private partial void LogTimelineFailed(Exception exception, string roomId);

    [LoggerMessage(LogLevel.Information, "Echoing a message of {Sender} in {RoomId}")]
    private partial void LogEchoing(string sender, string roomId);

    [LoggerMessage(LogLevel.Warning, "Echoing a message in {RoomId} failed")]
    private partial void LogEchoFailed(Exception exception, string roomId);
}

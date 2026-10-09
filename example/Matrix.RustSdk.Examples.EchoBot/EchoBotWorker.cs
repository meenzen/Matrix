using Matrix.RustSdk.Bindings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using LogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace Matrix.RustSdk.Examples.EchoBot;

/// <summary>
/// Logs in, joins every room the bot is invited to and answers text messages of other users with the same text, see
/// <see cref="RoomEcho"/>.
/// </summary>
public sealed partial class EchoBotWorker(IOptions<EchoBotOptions> options, ILogger<EchoBotWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);

    // the echo loops of the joined rooms, only used by the sync loop
    private readonly Dictionary<string, RoomEcho> _rooms = [];

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

        try
        {
            await SyncAsync(client, stoppingToken);
        }
        finally
        {
            foreach (RoomEcho room in _rooms.Values)
            {
                await room.DisposeAsync();
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
                // not cancelled, a join abandoned while the bot stops would add a room after the cleanup
                await JoinAsync(client, roomId);
            }
            foreach (string roomId in response.Rooms.Joined)
            {
                await ListenAsync(client, roomId);
            }
            foreach (string roomId in response.Rooms.Left.Where(_rooms.ContainsKey))
            {
                RoomEcho room = _rooms[roomId];
                _rooms.Remove(roomId);
                await room.DisposeAsync();
                LogLeft(roomId);
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

        // echo right away, the messages of the next sync are new
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
            _rooms[roomId] = await RoomEcho.StartAsync(room, logger);
        }
        catch (ClientException e)
        {
            LogTimelineFailed(e, roomId);
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
}

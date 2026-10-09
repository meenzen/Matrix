using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk.Examples.TuiClient;

/// <summary>
/// A room in the room list. It keeps the room id instead of the <see cref="Room"/>: the room list replaces its rooms
/// with new objects whenever something changes, <see cref="MatrixSession.GetRoom"/> gets the room when it is opened.
/// </summary>
public sealed record RoomSummary(string RoomId, string Name, bool IsInvite)
{
    public override string ToString() => IsInvite ? $"{Name} (invite)" : Name;
}

/// <summary>
/// The SDK side of the client: a logged in <see cref="Client"/>, the <see cref="SyncService"/> that keeps it up to date
/// and the room list. This class doesn't know anything about the UI, it reports changes on thread pool threads and the
/// UI has to move them to its main loop.
/// </summary>
public sealed class MatrixSession : IAsyncDisposable
{
    /// <summary>
    /// Updates the live lists on thread pool threads. Without it they would use the main loop of Terminal.Gui, which is
    /// gone when the session is disposed after the UI closed.
    /// </summary>
    internal static readonly SynchronizationContext ThreadPool = new();

    private const int RoomListPageSize = 200;

    private readonly Client _client;
    private readonly SyncService _syncService;
    private readonly CancellationTokenSource _stopping = new();
    private RoomListService? _roomListService;
    private RoomList? _roomList;
    private LiveList<RoomSummary>? _rooms;
    private Task? _syncStateWatcher;

    private MatrixSession(Client client, SyncService syncService)
    {
        _client = client;
        _syncService = syncService;
        UserId = client.UserId();
    }

    public string UserId { get; }

    /// <summary>
    /// The rooms, sorted by recency. Empty until <see cref="StartAsync"/> was called.
    /// </summary>
    public IReadOnlyList<RoomSummary> Rooms => _rooms?.ToArray() ?? [];

    /// <summary>
    /// Logs in with a password. The client uses an in-memory store, so every start is a new login (and a new device).
    /// Real clients use <see cref="ClientBuilder.SqliteStore"/> and restore the session instead.
    /// </summary>
    public static async Task<MatrixSession> LoginAsync(string homeserver, string username, string password)
    {
        // the SyncService uses simplified sliding sync (MSC4186), DiscoverNative checks that the server supports it
        Client client = await new ClientBuilder()
            .ServerNameOrHomeserverUrl(homeserver)
            .SlidingSyncVersionBuilder(SlidingSyncVersionBuilder.DiscoverNative)
            .InMemoryStore()
            .Build();
        try
        {
            await client.Login(username, password, initialDeviceName: "Matrix.RustSdk TUI client", deviceId: null);
            SyncService syncService = await client.SyncService().Finish();
            return new MatrixSession(client, syncService);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Starts syncing. <paramref name="onRoomsChanged"/> is called whenever <see cref="Rooms"/> changes and
    /// <paramref name="onSyncStateChanged"/> whenever the state of the sync service changes.
    /// </summary>
    public async Task StartAsync(System.Action onRoomsChanged, Action<SyncServiceState> onSyncStateChanged)
    {
        _syncStateWatcher = WatchSyncStateAsync(onSyncStateChanged);

        // the room list service is driven by the sync service, it yields the rooms sorted by recency. The query hides
        // spaces, left rooms and old versions of upgraded rooms, like Element X does.
        _roomListService = _syncService.RoomListService();
        _roomList = await _roomListService.AllRooms();
        _rooms = _roomList
            .WatchRoomDiffsAsync(new RoomListQuery(RoomListPageSize))
            .ToLiveList(Summarize, synchronizationContext: ThreadPool);
        // the list is updated on another thread and may have changed before the handler was attached, so it is
        // reported once right away
        _rooms.Changed += (_, _) => onRoomsChanged();
        onRoomsChanged();

        await _syncService.Start();
    }

    /// <summary>
    /// The room with <paramref name="roomId"/>, null if the client doesn't know it. The caller disposes it.
    /// </summary>
    public Room? GetRoom(string roomId) => _client.GetRoom(roomId);

    public async ValueTask DisposeAsync()
    {
        await _syncService.Stop();
        await _stopping.CancelAsync();
        if (_syncStateWatcher is not null)
        {
            // started by StartAsync, it ends with the cancellation
#pragma warning disable VSTHRD003
            await _syncStateWatcher;
#pragma warning restore VSTHRD003
        }
        if (_rooms is not null)
        {
            await _rooms.DisposeAsync();
        }
        _roomList?.Dispose();
        _roomListService?.Dispose();
        _syncService.Dispose();
        _stopping.Dispose();

        // the in-memory session can't be restored, remove the device from the account again
        try
        {
            await _client.Logout();
        }
        catch (ClientException)
        {
            // the server may be gone already, there's nothing left to clean up then
        }
        _client.Dispose();
    }

    private async Task WatchSyncStateAsync(Action<SyncServiceState> onSyncStateChanged)
    {
        try
        {
            await foreach (SyncServiceState state in _syncService.WatchStateAsync(_stopping.Token))
            {
                onSyncStateChanged(state);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            // the session is disposed
        }
    }

    // the room is disposed right after, only managed values may be kept
    private static RoomSummary Summarize(Room room) =>
        new(room.Id(), room.DisplayName() ?? room.Id(), room.Membership() == Membership.Invited);
}

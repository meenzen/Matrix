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
/// The SDK side of the client: a logged in <see cref="Client"/> with its session stored in the data directory
/// (<see cref="StoredClient"/>), the <see cref="SyncService"/> that keeps it up to date and the room list. This class doesn't know anything about the UI, it reports changes on thread pool threads and the
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

    private readonly StoredClient _stored;
    private readonly Client _client;
    private readonly SyncService _syncService;
    private readonly CancellationTokenSource _stopping = new();
    private RoomListService? _roomListService;
    private RoomList? _roomList;
    private LiveList<RoomSummary>? _rooms;
    private Task? _syncStateWatcher;

    private MatrixSession(StoredClient stored, SyncService syncService)
    {
        _stored = stored;
        _client = stored.Client;
        _syncService = syncService;
        UserId = _client.UserId();
    }

    public string UserId { get; }

    /// <summary>
    /// The rooms, sorted by recency. Empty until <see cref="StartAsync"/> was called.
    /// </summary>
    public IReadOnlyList<RoomSummary> Rooms => _rooms?.ToArray() ?? [];

    /// <summary>
    /// Restores the session stored in <paramref name="dataDirectory"/>, null if there is none and the user has to log
    /// in.
    /// </summary>
    public static async Task<MatrixSession?> TryRestoreAsync(string dataDirectory)
    {
        StoredClient? stored = await StoredClient.TryRestoreAsync(Store(dataDirectory));
        return stored is null ? null : await CreateAsync(stored);
    }

    /// <summary>
    /// Logs in with a password and stores the session in <paramref name="dataDirectory"/>, later starts restore it.
    /// </summary>
    public static async Task<MatrixSession> LoginAsync(
        string dataDirectory,
        string homeserver,
        string username,
        string password
    )
    {
        // the login checks that the homeserver supports simplified sliding sync (MSC4186), the SyncService needs it
        StoredClient stored = await StoredClient.LoginAsync(
            Store(dataDirectory),
            new PasswordCredentials
            {
                Homeserver = homeserver,
                Username = username,
                Password = password,
                DeviceName = "Matrix.RustSdk TUI client",
            }
        );
        return await CreateAsync(stored);
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
            .WatchRoomDiffsAsync(new RoomListQuery(RoomListPageSize), _stopping.Token)
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

    /// <summary>
    /// Stops syncing and logs out: removes the device from the account and deletes the stored session.
    /// </summary>
    public async Task LogoutAsync()
    {
        await StopAsync();
        try
        {
            await _stored.LogoutAsync();
        }
        finally
        {
            // a failed logout keeps the session, it is restored at the next start
            await _stored.DisposeAsync();
        }
    }

    /// <summary>
    /// Stops syncing and closes the client, the session stays stored.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        await _stored.DisposeAsync();
    }

    private static ClientStoreOptions Store(string dataDirectory) => new() { DataDirectory = dataDirectory };

    private static async Task<MatrixSession> CreateAsync(StoredClient stored)
    {
        try
        {
            SyncService syncService = await stored.Client.SyncService().Finish();
            return new MatrixSession(stored, syncService);
        }
        catch
        {
            await stored.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Stops the sync and disposes everything created from the client, which the <see cref="StoredClient"/> expects
    /// before it is disposed.
    /// </summary>
    private async Task StopAsync()
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

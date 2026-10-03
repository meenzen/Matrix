using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk.Examples.TuiClient;

/// <summary>
/// A room in the room list.
/// </summary>
public sealed record RoomSummary(Room Room, string Name, bool IsInvite)
{
    public override string ToString() => IsInvite ? $"{Name} (invite)" : Name;
}

/// <summary>
/// The SDK side of the client: a logged in <see cref="Client"/>, the <see cref="SyncService"/> that keeps it up to date
/// and the room list. This class doesn't know anything about the UI. The SDK calls the listeners on its own threads,
/// the UI has to move the updates to its main loop.
/// </summary>
public sealed class MatrixSession : IAsyncDisposable
{
    private const uint RoomListPageSize = 200;

    private readonly Client _client;
    private readonly SyncService _syncService;
    private TaskHandle? _syncStateHandle;
    private RoomList? _roomList;
    private RoomListEntriesWithDynamicAdaptersResult? _roomListEntries;

    private MatrixSession(Client client, SyncService syncService)
    {
        _client = client;
        _syncService = syncService;
        UserId = client.UserId();
    }

    public string UserId { get; }

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
    /// Starts syncing. <paramref name="onRoomsChanged"/> is called with the whole room list whenever it changes and
    /// <paramref name="onSyncStateChanged"/> whenever the state of the sync service changes.
    /// </summary>
    public async Task StartAsync(
        Action<IReadOnlyList<RoomSummary>> onRoomsChanged,
        Action<SyncServiceState> onSyncStateChanged
    )
    {
        _syncStateHandle = _syncService.State(new SyncStateObserver(onSyncStateChanged));

        // the room list service is driven by the sync service, it yields the rooms sorted by recency
        RoomListService roomListService = _syncService.RoomListService();
        _roomList = await roomListService.AllRooms();
        _roomListEntries = _roomList.EntriesWithDynamicAdapters(RoomListPageSize, new RoomListObserver(onRoomsChanged));
        // the entries stream doesn't emit anything until a filter is set
        _roomListEntries.Controller().SetFilter(new RoomListEntriesDynamicFilterKind.NonLeft());

        await _syncService.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _syncService.Stop();
        _roomListEntries?.EntriesStream().Cancel();
        _roomListEntries?.Dispose();
        _roomList?.Dispose();
        _syncStateHandle?.Cancel();
        _syncStateHandle?.Dispose();
        _syncService.Dispose();

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

    /// <summary>
    /// Keeps a copy of the room list and applies the diffs of the SDK to it.
    /// </summary>
    private sealed class RoomListObserver(Action<IReadOnlyList<RoomSummary>> onRoomsChanged) : RoomListEntriesListener
    {
        private readonly Lock _lock = new();
        private readonly List<RoomSummary> _rooms = [];

        public void OnUpdate(RoomListEntriesUpdate[] roomEntriesUpdate)
        {
            RoomSummary[] snapshot;
            lock (_lock)
            {
                foreach (RoomListEntriesUpdate update in roomEntriesUpdate)
                {
                    Apply(update);
                }
                snapshot = [.. _rooms];
            }
            onRoomsChanged(snapshot);
        }

        private void Apply(RoomListEntriesUpdate update)
        {
            switch (update)
            {
                case RoomListEntriesUpdate.Append append:
                    _rooms.AddRange(append.Values.Select(Summarize));
                    break;
                case RoomListEntriesUpdate.Clear:
                    _rooms.Clear();
                    break;
                case RoomListEntriesUpdate.PushFront pushFront:
                    _rooms.Insert(0, Summarize(pushFront.Value));
                    break;
                case RoomListEntriesUpdate.PushBack pushBack:
                    _rooms.Add(Summarize(pushBack.Value));
                    break;
                case RoomListEntriesUpdate.PopFront:
                    _rooms.RemoveAt(0);
                    break;
                case RoomListEntriesUpdate.PopBack:
                    _rooms.RemoveAt(_rooms.Count - 1);
                    break;
                case RoomListEntriesUpdate.Insert insert:
                    _rooms.Insert((int)insert.Index, Summarize(insert.Value));
                    break;
                case RoomListEntriesUpdate.Set set:
                    _rooms[(int)set.Index] = Summarize(set.Value);
                    break;
                case RoomListEntriesUpdate.Remove remove:
                    _rooms.RemoveAt((int)remove.Index);
                    break;
                case RoomListEntriesUpdate.Truncate truncate:
                    _rooms.RemoveRange((int)truncate.Length, _rooms.Count - (int)truncate.Length);
                    break;
                case RoomListEntriesUpdate.Reset reset:
                    _rooms.Clear();
                    _rooms.AddRange(reset.Values.Select(Summarize));
                    break;
            }
        }

        private static RoomSummary Summarize(Room room) =>
            new(room, room.DisplayName() ?? room.Id(), room.Membership() == Membership.Invited);
    }

    private sealed class SyncStateObserver(Action<SyncServiceState> onSyncStateChanged) : SyncServiceStateObserver
    {
        public void OnUpdate(SyncServiceState state) => onSyncStateChanged(state);
    }
}

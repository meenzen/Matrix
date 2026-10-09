using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Testing;

namespace Matrix.RustSdk.Tests;

/// <summary>
/// The room list helpers against a real homeserver.
/// </summary>
[Category(Homeserver.Category)]
[ClassDataSource<Homeserver>(Shared = SharedType.PerTestSession)]
public class RoomListTests(Homeserver homeserver)
{
    // updates the lists on thread pool threads, the tests don't depend on the context of the test runner
    private static readonly SynchronizationContext ThreadPool = new();

    [Test]
    public async Task Entries_ShouldContainTheRoomsAndFollowTheFilter()
    {
        // Arrange
        Client client = await homeserver.LoginAsync(await homeserver.CreateUserAsync("rooms"));
        string first = await CreateRoomAsync(client, "first");
        string second = await CreateRoomAsync(client, "second");
        await using SyncSession sync = await SyncSession.StartAsync(client);
        RoomListQuery query = new(pageSize: 10);

        // Act
        await using LiveList<string> rooms = sync
            .RoomList.WatchEntryDiffsAsync(query)
            .ToLiveList(room => room.Id(), synchronizationContext: ThreadPool);
        await Poll.UntilAsync(
            () => Task.FromResult(rooms.Contains(first) && rooms.Contains(second)),
            "both rooms are in the list"
        );
        query.Filter = new RoomListEntriesDynamicFilterKind.Identifiers([second]);
        await Poll.UntilAsync(() => Task.FromResult(rooms.Count == 1), "the filter is applied");

        // Assert
        await Assert.That(rooms.ToArray()).IsEquivalentTo([second]);
        await Assert.That(rooms.Completion.IsCompleted).IsFalse();
    }

    [Test]
    public async Task LoadingState_ShouldBecomeLoaded()
    {
        // Arrange
        Client client = await homeserver.LoginAsync(await homeserver.CreateUserAsync("loading"));
        await CreateRoomAsync(client, "loading");
        await using SyncSession sync = await SyncSession.StartAsync(client);
        using CancellationTokenSource timeout = new(Poll.DefaultTimeout);
        RoomListLoadingState? state = null;

        // Act
        await foreach (RoomListLoadingState current in sync.RoomList.WatchLoadingStateAsync(timeout.Token))
        {
            state = current;
            if (current is RoomListLoadingState.Loaded)
            {
                break;
            }
        }

        // Assert
        await Assert.That(state).IsTypeOf<RoomListLoadingState.Loaded>();
    }

    [Test]
    public async Task Query_ShouldOnlyBeUsedByOneEnumerationAtATime()
    {
        // Arrange
        Client client = await homeserver.LoginAsync(await homeserver.CreateUserAsync("query"));
        string room = await CreateRoomAsync(client, "query");
        await using SyncSession sync = await SyncSession.StartAsync(client);
        RoomListQuery query = new(pageSize: 10);
        LiveList<string> rooms = sync
            .RoomList.WatchEntryDiffsAsync(query)
            .ToLiveList(entry => entry.Id(), synchronizationContext: ThreadPool);
        await rooms.Initialized.WaitAsync(Poll.DefaultTimeout);

        // Act
        async Task EnumerateAsync()
        {
            await foreach (VectorDiff<Room>[] diffs in sync.RoomList.WatchEntryDiffsAsync(query))
            {
                // the query is in use, the enumeration throws before the first value
                _ = diffs;
            }
        }
        Exception? whileInUse = null;
        try
        {
            await EnumerateAsync();
        }
        catch (InvalidOperationException e)
        {
            whileInUse = e;
        }
        await rooms.DisposeAsync();
        await using LiveList<string> again = sync
            .RoomList.WatchEntryDiffsAsync(query)
            .ToLiveList(entry => entry.Id(), synchronizationContext: ThreadPool);
        await Poll.UntilAsync(() => Task.FromResult(again.Contains(room)), "the query works again");

        // Assert
        await Assert.That(whileInUse).IsNotNull();
    }

    private static async Task<string> CreateRoomAsync(Client client, string name) =>
        await client.CreateRoom(
            new CreateRoomParameters(
                Name: name,
                IsEncrypted: false,
                Visibility: new RoomVisibility.Private(),
                Preset: RoomPreset.PrivateChat
            )
        );

    /// <summary>
    /// A running sync service and its room list of all rooms.
    /// </summary>
    private sealed class SyncSession(SyncService syncService, RoomListService roomListService, RoomList roomList)
        : IAsyncDisposable
    {
        public RoomList RoomList { get; } = roomList;

        public static async Task<SyncSession> StartAsync(Client client)
        {
            using SyncServiceBuilder builder = client.SyncService();
            SyncService syncService = await builder.Finish();
            RoomListService roomListService = syncService.RoomListService();
            RoomList roomList = await roomListService.AllRooms();
            await syncService.Start();
            return new SyncSession(syncService, roomListService, roomList);
        }

        public async ValueTask DisposeAsync()
        {
            await syncService.Stop();
            RoomList.Dispose();
            roomListService.Dispose();
            syncService.Dispose();
        }
    }
}

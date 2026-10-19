using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Examples.TuiClient.Chat;
using Matrix.RustSdk.Testing;
using Timeline = Matrix.RustSdk.Bindings.Timeline;

namespace Matrix.RustSdk.Examples.TuiClient.Tests.Support;

/// <summary>
/// Another user the TUI client talks to, using the SDK directly: creates rooms, sends messages and checks what the
/// TUI client did. Timelines are watched as <see cref="TimelineEntry"/>s, like the client shows them.
/// </summary>
public sealed class Peer : IAsyncDisposable
{
    private static readonly SynchronizationContext ThreadPool = new();

    private readonly Dictionary<string, (Timeline Timeline, LiveList<TimelineEntry> Entries)> _timelines = [];

    private Peer(TestUser user, Client client, SyncService syncService)
    {
        User = user;
        Client = client;
        SyncService = syncService;
    }

    public TestUser User { get; }

    public Client Client { get; }

    public SyncService SyncService { get; }

    public static async Task<Peer> StartAsync(Homeserver homeserver, string prefix = "peer", TestUser? user = null)
    {
        user ??= await homeserver.CreateUserAsync(prefix);
        Client client = await homeserver.LoginAsync(user);
        SyncService syncService = await client.SyncService().Finish();
        await syncService.Start();
        return new Peer(user, client, syncService);
    }

    public async Task<string> CreateRoomAsync(
        string name,
        string[]? invite = null,
        bool isEncrypted = false,
        bool isPublic = false,
        string? alias = null,
        bool isDirect = false
    )
    {
        string roomId = await Client.CreateRoom(
            new CreateRoomParameters(
                Name: name,
                IsEncrypted: isEncrypted,
                Visibility: isPublic ? new RoomVisibility.Public() : new RoomVisibility.Private(),
                Preset: isPublic ? RoomPreset.PublicChat : RoomPreset.PrivateChat,
                Invite: invite,
                CanonicalAlias: alias,
                IsDirect: isDirect
            )
        );
        using Room room = await Client.AwaitRoomRemoteEcho(roomId);
        return roomId;
    }

    /// <summary>
    /// The room, waiting until the sync knows it. The caller disposes it.
    /// </summary>
    public Task<Room> GetRoomAsync(string roomId) =>
        Poll.UntilAsync(() => Task.FromResult(Client.GetRoom(roomId)), $"the room {roomId} to sync");

    /// <summary>
    /// The timeline of the room as the TUI client would show it, kept up to date until the peer is disposed.
    /// </summary>
    public async Task<LiveList<TimelineEntry>> WatchAsync(string roomId) => (await OpenAsync(roomId)).Entries;

    public async Task<Timeline> TimelineAsync(string roomId) => (await OpenAsync(roomId)).Timeline;

    private async Task<(Timeline Timeline, LiveList<TimelineEntry> Entries)> OpenAsync(string roomId)
    {
        if (_timelines.TryGetValue(roomId, out (Timeline, LiveList<TimelineEntry>) opened))
        {
            return opened;
        }
        using Room room = await GetRoomAsync(roomId);
        Timeline timeline = await room.Timeline();
        LiveList<TimelineEntry> entries = timeline
            .WatchItemDiffsAsync()
            .ToLiveList(item => TimelineEntries.From(item, User.UserId), synchronizationContext: ThreadPool);
        _timelines[roomId] = (timeline, entries);
        return (timeline, entries);
    }

    public async Task SendAsync(string roomId, string text)
    {
        Timeline timeline = await TimelineAsync(roomId);
        await timeline.SendTextAsync(text);
    }

    /// <summary>
    /// Waits for an invite to a room whose name contains <paramref name="name"/> (any invite if it is null) and
    /// returns the id of the room.
    /// </summary>
    public async Task<string> WaitForInviteAsync(string? name = null) =>
        await Poll.UntilAsync(
            () =>
            {
                foreach (Room room in Client.Rooms())
                {
                    using (room)
                    {
                        if (
                            room.Membership() == Membership.Invited
                            && (name is null || (room.DisplayName() ?? "").Contains(name, StringComparison.Ordinal))
                        )
                        {
                            return Task.FromResult<string?>(room.Id());
                        }
                    }
                }
                return Task.FromResult<string?>(null);
            },
            $"an invite to {name ?? "a room"}"
        ) ?? "";

    /// <summary>
    /// Waits until the membership of <paramref name="userId"/> in the room satisfies <paramref name="predicate"/>.
    /// </summary>
    public async Task WaitForMembershipAsync(
        string roomId,
        string userId,
        Func<MembershipState?, bool> predicate,
        string description
    )
    {
        using Room room = await GetRoomAsync(roomId);
        await Poll.UntilAsync(
            async () =>
            {
                RoomMember[] members = await room.GetMembersAsync();
                return predicate(members.FirstOrDefault(m => m.UserId == userId)?.Membership);
            },
            description
        );
    }

    /// <summary>
    /// Waits for an entry matching <paramref name="predicate"/> in <paramref name="entries"/> and returns it.
    /// </summary>
    public static Task<TimelineEntry> WaitForEntryAsync(
        LiveList<TimelineEntry> entries,
        Func<TimelineEntry, bool> predicate,
        string description
    ) => Poll.UntilAsync(() => Task.FromResult(entries.ToArray().LastOrDefault(predicate)), description);

    public async ValueTask DisposeAsync()
    {
        foreach ((Timeline timeline, LiveList<TimelineEntry> entries) in _timelines.Values)
        {
            await entries.DisposeAsync();
            timeline.Dispose();
        }
        try
        {
            // a Stop right after the start can be lost and never return (matrix-rust-sdk race), the homeserver
            // disposes the client anyway
            await SyncService.Stop().WaitAsync(TimeSpan.FromSeconds(10));
        }
        catch (TimeoutException)
        {
            // the sync keeps running until the client is disposed
        }
        SyncService.Dispose();
    }
}

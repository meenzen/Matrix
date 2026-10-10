using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Examples.TuiClient.Rooms;
using Matrix.RustSdk.Examples.TuiClient.Security;

namespace Matrix.RustSdk.Examples.TuiClient;

/// <summary>
/// A message that should be shown as a desktop notification, see <see cref="MatrixSession.NotificationReceived"/>.
/// </summary>
public sealed record IncomingNotification(
    string RoomId,
    string RoomName,
    string Sender,
    string Body,
    bool IsDirect,
    bool HasMention,
    DateTimeOffset Time,
    bool IsNoisy = false
);

/// <summary>
/// The SDK side of the client: a logged in <see cref="Client"/> with its session stored in the data directory
/// (<see cref="StoredClient"/>), the <see cref="SyncService"/> that keeps it up to date, the room list and the
/// encryption state. This class doesn't know anything about the UI, it reports changes with events on thread pool
/// threads and the UI has to move them to its main loop.
/// </summary>
public sealed class MatrixSession : IAsyncDisposable
{
    /// <summary>
    /// Updates the live lists on thread pool threads. Without it they would use the main loop of Terminal.Gui, which is
    /// gone when the session is disposed after the UI closed.
    /// </summary>
    internal static readonly SynchronizationContext ThreadPool = new();

    private const int RoomListPageSize = 500;
    private static readonly TimeSpan SyncStopTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SyncRestartDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SendRetryDelay = TimeSpan.FromSeconds(10);

    private readonly StoredClient _stored;
    private readonly Client _client;
    private readonly SyncService _syncService;
    private readonly CancellationTokenSource _stopping = new();
    private readonly RoomListQuery _query = new(RoomListPageSize);
    private readonly List<Task> _watchers = [];
    private RoomListService? _roomListService;
    private RoomList? _roomList;
    private LiveList<RoomSummary>? _rooms;
    private bool _stopped;
    private Task _starting = Task.CompletedTask;
    private SyncServiceState? _syncState;
    private int _sendRetryScheduled;

    private MatrixSession(StoredClient stored, SyncService syncService, string dataDirectory)
    {
        _stored = stored;
        _client = stored.Client;
        _syncService = syncService;
        UserId = _client.UserId();
        DeviceId = _client.DeviceId();
        DataDirectory = dataDirectory;
        Encryption = new EncryptionState(_client);
        Verification = new VerificationFlow(_client);
    }

    public string UserId { get; }

    public string DeviceId { get; }

    /// <summary>
    /// Where the session is stored, the client keeps its own files there too.
    /// </summary>
    public string DataDirectory { get; }

    public Client Client => _client;

    /// <summary>
    /// The state of the key backup, recovery and the verification of this session.
    /// </summary>
    public EncryptionState Encryption { get; }

    /// <summary>
    /// Verifying this session with another one, or other users.
    /// </summary>
    public VerificationFlow Verification { get; }

    /// <summary>
    /// The rooms matching <see cref="RoomFilter"/>, sorted by recency. Empty until <see cref="StartAsync"/> was called.
    /// </summary>
    public IReadOnlyList<RoomSummary> Rooms => _rooms?.ToArray() ?? [];

    /// <summary>
    /// Raised whenever <see cref="Rooms"/> changes.
    /// </summary>
    public event EventHandler? RoomsChanged;

    public event EventHandler<SyncServiceState>? SyncStateChanged;

    /// <summary>
    /// Raised for messages the push rules of the user want a notification for, which arrived while syncing.
    /// </summary>
    public event EventHandler<IncomingNotification>? NotificationReceived;

    /// <summary>
    /// Raised when sending a message in a room failed: the room id and the error. Sending is retried on its own after
    /// a while and when the connection comes back, <see cref="RetrySendingAsync"/> retries right away.
    /// </summary>
    public event EventHandler<(string RoomId, string Error)>? SendFailed;

    /// <summary>
    /// Shows only the rooms whose name matches the value (fuzzy, like the search of Element X), all
    /// rooms if it is empty.
    /// </summary>
    public string RoomFilter
    {
        get;
        set
        {
            field = value;
            _query.Filter = string.IsNullOrWhiteSpace(value)
                ? null
                : new RoomListEntriesDynamicFilterKind.FuzzyMatchRoomName(value.Trim());
        }
    } = "";

    /// <summary>
    /// Restores the session stored in <paramref name="dataDirectory"/>, null if there is none and the user has to log
    /// in.
    /// </summary>
    public static async Task<MatrixSession?> TryRestoreAsync(string dataDirectory)
    {
        await ThreadPoolHop.Yield();
        StoredClient? stored = await StoredClient.TryRestoreAsync(Store(dataDirectory));
        return stored is null ? null : await CreateAsync(stored, dataDirectory);
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
        await ThreadPoolHop.Yield();
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
        return await CreateAsync(stored, dataDirectory);
    }

    /// <summary>
    /// Starts syncing and watching the room list and the encryption state.
    /// </summary>
    public Task StartAsync()
    {
        // disposing waits for the start, so it doesn't race with it
        _starting = StartCoreAsync();
        return _starting;
    }

    private async Task StartCoreAsync()
    {
        await ThreadPoolHop.Yield();
        CancellationToken stopping = _stopping.Token;
        _watchers.Add(WatchAsync(_syncService.WatchStateAsync(_stopping.Token), OnSyncStateChanged));
        _watchers.Add(
            WatchAsync(
                _client.WatchSendQueueErrorsAsync(_stopping.Token),
                failure =>
                {
                    SendFailed?.Invoke(this, (failure.RoomId, failure.Error.Message));
                    ScheduleSendRetry();
                }
            )
        );

        // notifications are evaluated while syncing, the handler has to be registered before the sync starts
        await _client.RegisterNotificationHandler(new NotificationListener(this));
        stopping.ThrowIfCancellationRequested();

        // the room list service is driven by the sync service, it yields the rooms sorted by recency. The query hides
        // spaces, left rooms and old versions of upgraded rooms, like Element X does.
        _roomListService = _syncService.RoomListService();
        _roomList = await _roomListService.AllRooms();
        stopping.ThrowIfCancellationRequested();
        _rooms = _roomList
            .WatchRoomDiffsAsync(_query, _stopping.Token)
            .ToLiveList<Room, RoomSummary>(SummarizeAsync, synchronizationContext: ThreadPool);
        // the list is updated on another thread and may have changed before the handler was attached, so it is
        // reported once right away
        _rooms.Changed += (_, _) => RoomsChanged?.Invoke(this, EventArgs.Empty);
        RoomsChanged?.Invoke(this, EventArgs.Empty);

        stopping.ThrowIfCancellationRequested();
        await _syncService.Start();

        _watchers.Add(Encryption.StartAsync(stopping));
        _watchers.Add(Verification.StartAsync(stopping));
    }

    /// <summary>
    /// Sends the messages that are waiting because sending failed. The send queue of a room stops after an error until
    /// it is enabled again; messages the server rejected for good stay failed.
    /// </summary>
    public async Task RetrySendingAsync()
    {
        await ThreadPoolHop.Yield();
        await _client.EnableAllSendQueues(true);
    }

    private void OnSyncStateChanged(SyncServiceState state)
    {
        SyncServiceState? previous = _syncState;
        _syncState = state;
        SyncStateChanged?.Invoke(this, state);
        switch (state)
        {
            // back online: the send queues stopped while the homeserver couldn't be reached
            case SyncServiceState.Running when previous is SyncServiceState.Offline or SyncServiceState.Error:
                _ = RunInBackgroundAsync(RetrySendingAsync);
                break;
            // the offline mode covers network errors, other errors stop the sync, which is started again
            case SyncServiceState.Error:
                _ = RunInBackgroundAsync(RestartSyncAsync);
                break;
        }
    }

    private async Task RestartSyncAsync()
    {
        await Task.Delay(SyncRestartDelay, _stopping.Token);
        await _syncService.Start();
    }

    /// <summary>
    /// Enables the send queues again a while after an error, once at a time.
    /// </summary>
    private void ScheduleSendRetry()
    {
        if (Interlocked.Exchange(ref _sendRetryScheduled, 1) == 1)
        {
            return;
        }
        _ = RunInBackgroundAsync(async () =>
        {
            try
            {
                await Task.Delay(SendRetryDelay, _stopping.Token);
            }
            finally
            {
                Volatile.Write(ref _sendRetryScheduled, 0);
            }
            await RetrySendingAsync();
        });
    }

    /// <summary>
    /// Runs a background task of the session, its errors are dropped: the next attempt or the user tries again.
    /// </summary>
    private static async Task RunInBackgroundAsync(Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (Exception e) when (e is OperationCanceledException or ClientException or ObjectDisposedException)
        {
            // stopped, or the next attempt tries again
        }
    }

    /// <summary>
    /// The room with <paramref name="roomId"/>, null if the client doesn't know it. The caller disposes it.
    /// </summary>
    public Room? GetRoom(string roomId) => _client.GetRoom(roomId);

    /// <summary>
    /// Joins a room by its id or an alias and returns its id.
    /// </summary>
    public async Task<string> JoinAsync(string roomIdOrAlias)
    {
        await ThreadPoolHop.Yield();
        using Room room = await _client.JoinRoomByIdOrAlias(roomIdOrAlias, []);
        return room.Id();
    }

    /// <summary>
    /// Creates a room, the creator joins it right away. Returns the id of the room.
    /// </summary>
    public async Task<string> CreateRoomAsync(string name, bool isEncrypted, bool isPublic)
    {
        await ThreadPoolHop.Yield();
        string roomId = await _client.CreateRoom(
            new CreateRoomParameters(
                Name: name,
                IsEncrypted: isEncrypted,
                Visibility: isPublic ? new RoomVisibility.Public() : new RoomVisibility.Private(),
                Preset: isPublic ? RoomPreset.PublicChat : RoomPreset.PrivateChat
            )
        );
        // the room is only known once the sync returned it
        using Room room = await _client.AwaitRoomRemoteEcho(roomId);
        return roomId;
    }

    /// <summary>
    /// The direct chat with <paramref name="userId"/>: an existing one, otherwise a new encrypted room the user is
    /// invited to. Returns the id of the room.
    /// </summary>
    public async Task<string> GetOrCreateDirectMessageAsync(string userId)
    {
        await ThreadPoolHop.Yield();
        using (Room? existing = _client.GetDmRoom(userId))
        {
            if (existing is not null)
            {
                return existing.Id();
            }
        }
        string roomId = await _client.CreateRoom(
            new CreateRoomParameters(
                Name: null,
                IsEncrypted: true,
                Visibility: new RoomVisibility.Private(),
                Preset: RoomPreset.TrustedPrivateChat,
                IsDirect: true,
                Invite: [userId]
            )
        );
        using Room room = await _client.AwaitRoomRemoteEcho(roomId);
        return roomId;
    }

    public async Task IgnoreAsync(string userId)
    {
        await ThreadPoolHop.Yield();
        await _client.IgnoreUser(userId);
    }

    public async Task UnignoreAsync(string userId)
    {
        await ThreadPoolHop.Yield();
        await _client.UnignoreUser(userId);
    }

    /// <summary>
    /// Stops syncing and logs out: removes the device from the account and deletes the stored session.
    /// </summary>
    public async Task LogoutAsync()
    {
        await ThreadPoolHop.Yield();
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
        await ThreadPoolHop.Yield();
        await StopAsync();
        await _stored.DisposeAsync();
    }

    private static ClientStoreOptions Store(string dataDirectory) =>
        new()
        {
            DataDirectory = dataDirectory,
            // like Element X: a new account gets cross-signing keys and a key backup right away, room keys missing
            // after a login are downloaded from the backup when a message can't be decrypted
            ConfigureClient = builder =>
                builder
                    .AutoEnableCrossSigning(true)
                    .AutoEnableBackups(true)
                    .BackupDownloadStrategy(Bindings.Sdk.BackupDownloadStrategy.AfterDecryptionFailure),
        };

    private static async Task<MatrixSession> CreateAsync(StoredClient stored, string dataDirectory)
    {
        try
        {
            // the offline mode waits for the homeserver to come back after network errors instead of stopping
            SyncService syncService = await stored.Client.SyncService().WithOfflineMode().Finish();
            return new MatrixSession(stored, syncService, dataDirectory);
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
        if (_stopped)
        {
            return;
        }
        _stopped = true;
        await _stopping.CancelAsync();
        try
        {
            // started by this class, they end with the cancellation, their errors don't matter anymore
#pragma warning disable VSTHRD003
            await _starting.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            try
            {
                // a Stop right after the start can be lost and never return (matrix-rust-sdk race)
                await _syncService.Stop().WaitAsync(SyncStopTimeout, CancellationToken.None);
            }
            catch (Exception e) when (e is TimeoutException or ClientException)
            {
                // the client is closed anyway
            }
            await Task.WhenAll(_watchers).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
#pragma warning restore VSTHRD003
        }
        finally
        {
            // everything created from the client is disposed before the StoredClient
            if (_rooms is not null)
            {
                await _rooms.DisposeAsync();
            }
            Verification.Dispose();
            Encryption.Dispose();
            _roomList?.Dispose();
            _roomListService?.Dispose();
            _syncService.Dispose();
            _stopping.Dispose();
        }
    }

    private static async Task WatchAsync<T>(IAsyncEnumerable<T> values, Action<T> onValue)
    {
        try
        {
            await foreach (T value in values)
            {
                onValue(value);
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ClientException or ObjectDisposedException)
        {
            // the session is disposed, or the subscription failed: there are no more values
        }
    }

    // the room is disposed right after, only managed values may be kept. It must not throw, that would stop the list.
    private static async ValueTask<RoomSummary> SummarizeAsync(Room room, CancellationToken cancellationToken)
    {
        try
        {
            using RoomInfo info = await room.RoomInfo();
            return RoomSummary.From(info);
        }
        catch (ClientException)
        {
            string id = room.Id();
            return new RoomSummary(id, room.DisplayName() ?? id);
        }
    }

    /// <summary>
    /// Forwards the notifications of the SDK. It runs on a thread of the SDK and must never throw, an exception would
    /// become a Rust panic.
    /// </summary>
    private sealed class NotificationListener(MatrixSession session) : SyncNotificationListener
    {
        public void OnNotification(NotificationItem notification, string roomId)
        {
            try
            {
                using (notification)
                {
                    if (ToIncoming(notification, roomId) is { } incoming)
                    {
                        session.NotificationReceived?.Invoke(session, incoming);
                    }
                }
            }
#pragma warning disable CA1031 // a notification that can't be shown is skipped
            catch (Exception)
#pragma warning restore CA1031
            {
                // skipped
            }
        }

        private IncomingNotification? ToIncoming(NotificationItem notification, string roomId)
        {
            string sender = notification.SenderInfo.DisplayName ?? "someone";
            string room = notification.RoomInfo.DisplayName;
            bool isDirect = notification.RoomInfo.IsDm || notification.RoomInfo.IsDirect;
            bool hasMention = notification.HasMention ?? false;
            switch (notification.Event)
            {
                case NotificationEvent.Invite invite:
                    return new IncomingNotification(
                        roomId,
                        room,
                        invite.Sender,
                        "invited you",
                        isDirect,
                        HasMention: true,
                        DateTimeOffset.UtcNow
                    );
                case NotificationEvent.Timeline { Event: var timelineEvent }:
                    if (timelineEvent.SenderId() == session.UserId)
                    {
                        return null;
                    }
                    using (TimelineEventContent content = timelineEvent.Content())
                    {
                        string body = content switch
                        {
                            TimelineEventContent.MessageLike
                            {
                                Content: MessageLikeEventContent.RoomMessage { MessageType: var type }
                            } => BodyOf(type),
                            TimelineEventContent.MessageLike { Content: MessageLikeEventContent.RoomEncrypted } =>
                                "(encrypted message)",
                            _ => "",
                        };
                        if (body.Length == 0)
                        {
                            return null;
                        }
                        return new IncomingNotification(
                            roomId,
                            room,
                            sender,
                            body,
                            isDirect,
                            hasMention,
                            Chat.TimelineEntries.FromTimestamp(timelineEvent.Timestamp()),
                            // the push rules want a sound, like for direct messages
                            notification.IsNoisy
                                ?? false
                        );
                    }
                default:
                    return null;
            }
        }

        private static string BodyOf(MessageType type) =>
            type switch
            {
                MessageType.Text text => text.Content.Body,
                MessageType.Notice notice => notice.Content.Body,
                MessageType.Emote emote => $"* {emote.Content.Body}",
                MessageType.Image => "sent an image",
                MessageType.Video => "sent a video",
                MessageType.Audio => "sent an audio message",
                MessageType.File => "sent a file",
                MessageType.Other other => other.Body,
                _ => "sent a message",
            };
    }
}

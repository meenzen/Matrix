using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Testing;

namespace Matrix.RustSdk.Tests;

/// <summary>
/// The generated <see cref="IAsyncEnumerable{T}"/> subscriptions against a real homeserver.
/// </summary>
[Category(Homeserver.Category)]
[ClassDataSource<Homeserver>(Shared = SharedType.PerTestSession)]
public class SubscriptionTests(Homeserver homeserver)
{
    [Test]
    public async Task TimelineUpdates_ShouldContainSentMessage()
    {
        // Arrange
        const string body = "hello from a generated subscription";
        using Room room = await CreateRoomAsync("timeline");
        using Timeline timeline = await room.Timeline();
        using CancellationTokenSource timeout = new(Poll.DefaultTimeout);
        bool sent = false;
        bool received = false;

        // Act
        await foreach (VectorDiff<TimelineItem>[] diffs in timeline.WatchItemDiffsAsync(timeout.Token))
        {
            received = diffs.SelectMany(Items).Any(item => IsText(item, body));
            foreach (VectorDiff<TimelineItem> diff in diffs)
            {
                diff.Dispose();
            }
            if (received)
            {
                break;
            }

            // the first batch is the reset with the current items, everything after it is live
            if (!sent)
            {
                sent = true;
                using RoomMessageEventContentWithoutRelation content = MatrixSdkFfiMethods.MessageEventContentNew(
                    new MessageType.Text(new TextMessageContent(body, Formatted: null))
                );
                using SendHandle _ = await timeline.Send(content);
            }
        }

        // Assert
        await Assert.That(received).IsTrue();
    }

    [Test]
    public async Task SyncServiceStateChanges_ShouldReportRunning()
    {
        // Arrange
        Client client = await homeserver.LoginAsync(await homeserver.CreateUserAsync("sync"));
        using SyncServiceBuilder builder = client.SyncService();
        using SyncService syncService = await builder.Finish();
        using CancellationTokenSource timeout = new(Poll.DefaultTimeout);
        SyncServiceState? state = null;

        // Act
        await syncService.Start();
        try
        {
            await foreach (SyncServiceState current in syncService.WatchStateAsync(timeout.Token))
            {
                state = current;
                if (current == SyncServiceState.Running)
                {
                    break;
                }
            }

            // Start reports Running before the sync tasks ran once, they only subscribe to the stop signal then. A
            // Stop before that is lost and never returns (matrix-rust-sdk race, the tests share a single threaded
            // runtime, which makes it likely). The room list service leaves Initial after its first sync
            using RoomListService roomListService = syncService.RoomListService();
            await roomListService
                .WatchStateAsync(timeout.Token)
                .FirstAsync(roomListState => roomListState != RoomListServiceState.Initial, timeout.Token);
        }
        finally
        {
            await syncService.Stop();
        }

        // Assert
        await Assert.That(state).IsEqualTo(SyncServiceState.Running);
    }

    [Test]
    public async Task TypingUsers_ShouldThrowWhenCancelled()
    {
        // Arrange
        using Room room = await CreateRoomAsync("typing");
        using CancellationTokenSource cancellation = new(TimeSpan.FromMilliseconds(500));
        OperationCanceledException? exception = null;

        // Act
        try
        {
            await foreach (string[] _ in room.WatchTypingUsersAsync(cancellation.Token))
            {
                // nobody types and nothing syncs, the enumeration waits until it is cancelled
            }
        }
        catch (OperationCanceledException e)
        {
            exception = e;
        }

        // Assert
        await Assert.That(exception).IsNotNull();
    }

    [Test]
    public async Task RoomInfo_ShouldStartWithTheCurrentInfo()
    {
        // Arrange
        using Room room = await CreateRoomAsync("info");
        using CancellationTokenSource timeout = new(Poll.DefaultTimeout);

        // Act
        // nothing changes and nothing syncs, the SDK alone wouldn't yield anything
        using RoomInfo info = await room.WatchRoomInfoAsync(timeout.Token).FirstAsync(timeout.Token);

        // Assert
        await Assert.That(info.Id).IsEqualTo(room.Id());
    }

    [Test]
    public async Task SyncIndicator_ShouldRejectNegativeDelays()
    {
        // Arrange
        Client client = await homeserver.LoginAsync(await homeserver.CreateUserAsync("indicator"));
        using SyncServiceBuilder builder = client.SyncService();
        using SyncService syncService = await builder.Finish();
        using RoomListService roomListService = syncService.RoomListService();

        // Act
        IAsyncEnumerable<RoomListServiceSyncIndicator> Watch() =>
            roomListService.WatchSyncIndicatorAsync(TimeSpan.FromSeconds(-1), TimeSpan.Zero);

        // Assert
        await Assert.That(Watch).Throws<ArgumentOutOfRangeException>().WithParameterName("delayBeforeShowing");
    }

    private async Task<Room> CreateRoomAsync(string prefix)
    {
        Client client = await homeserver.LoginAsync(await homeserver.CreateUserAsync(prefix));
        string roomId = await client.CreateRoom(
            new CreateRoomParameters(
                Name: prefix,
                IsEncrypted: false,
                Visibility: new RoomVisibility.Private(),
                Preset: RoomPreset.PrivateChat
            )
        );
        return client.GetRoom(roomId) ?? throw new InvalidOperationException($"{roomId} is unknown after creating it.");
    }

    private static IEnumerable<TimelineItem> Items(VectorDiff<TimelineItem> diff) =>
        diff switch
        {
            VectorDiff<TimelineItem>.Append append => append.Values,
            VectorDiff<TimelineItem>.Reset reset => reset.Values,
            VectorDiff<TimelineItem>.PushBack pushBack => [pushBack.Value],
            VectorDiff<TimelineItem>.PushFront pushFront => [pushFront.Value],
            VectorDiff<TimelineItem>.Insert insert => [insert.Value],
            VectorDiff<TimelineItem>.Set set => [set.Value],
            _ => [],
        };

    private static bool IsText(TimelineItem item, string body)
    {
        using EventTimelineItem? timelineEvent = item.AsEvent();
        return timelineEvent?.Content
                is TimelineItemContent.MsgLike
                {
                    Content.Kind: MsgLikeKind.Message { Content.MsgType: MessageType.Text text }
                }
            && text.Content.Body == body;
    }
}

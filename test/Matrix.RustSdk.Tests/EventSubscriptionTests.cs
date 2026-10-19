using System.Text.Json;
using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Bindings.RumaEvents;
using Matrix.RustSdk.Testing;

namespace Matrix.RustSdk.Tests;

/// <summary>
/// The subscriptions to raw events (message-like, state, sticky and to-device) against a real homeserver.
/// </summary>
[Category(Homeserver.Category)]
[ClassDataSource<Homeserver>(Shared = SharedType.PerTestSession)]
public class EventSubscriptionTests(Homeserver homeserver)
{
    private const string CustomType = "org.example.test";
    private const string SpaceChild = "m.space.child";
    private const string Via = """{"via":["localhost"]}""";

    [Test]
    public async Task MessageLikeEvents_ShouldYieldSyncedEventsOfTheType()
    {
        // Arrange
        await using SyncingClient alice = await SyncingClient.StartAsync(homeserver, "message-like");
        using Room room = await alice.CreateRoomAsync();
        using MessageLikeEventType eventType = RumaEventsMethods.MessageLikeEventTypeFromString(CustomType);
        using CancellationTokenSource timeout = new(Poll.DefaultTimeout);

        // Act
        // subscribes when the enumeration starts, before the events are sent
        Task<RoomMessageLikeEvent> received = room.WatchMessageLikeEventsAsync(eventType, timeout.Token)
            .FirstAsync(timeout.Token)
            .AsTask();
        await room.SendRaw("org.example.other", """{"value":"other"}""");
        string eventId = await room.SendRaw(CustomType, """{"value":"custom"}""");
        RoomMessageLikeEvent message = await received;

        // Assert
        await Assert.That(message.EventId).IsEqualTo(eventId);
        await Assert.That(message.Sender).IsEqualTo(alice.Client.UserId());
        await Assert.That(Value(message.ContentJson)).IsEqualTo("custom");
        await Assert.That(message.EventType).IsSameReferenceAs(eventType);
    }

    [Test]
    public async Task StateEvents_ShouldStartWithTheCurrentEventsAndYieldChanges()
    {
        // Arrange
        await using SyncingClient alice = await SyncingClient.StartAsync(homeserver, "state");
        using Room room = await alice.CreateRoomAsync();
        // custom state types aren't part of the state the sync service requests, space children are
        using StateEventType eventType = new StateEventType.SpaceChild();
        using CancellationTokenSource timeout = new(Poll.DefaultTimeout);
        await room.SendStateEventRaw(SpaceChild, "!first:localhost", Via);
        // waits until the event is synced
        await room.WatchStateEventsAsync(eventType, timeout.Token)
            .FirstAsync(events => events.Length == 1, timeout.Token);
        List<RoomStateEvent[]> snapshots = [];

        // Act
        await foreach (RoomStateEvent[] events in room.WatchStateEventsAsync(eventType, timeout.Token))
        {
            snapshots.Add(events);
            if (snapshots.Count == 1)
            {
                await room.SendStateEventRaw(SpaceChild, "!second:localhost", Via);
            }
            if (events.Length == 2)
            {
                break;
            }
        }

        // Assert
        await Assert.That(snapshots[0].Select(e => e.StateKey)).IsEquivalentTo(["!first:localhost"]);
        await Assert
            .That(snapshots[^1].Select(e => e.StateKey))
            .IsEquivalentTo(["!first:localhost", "!second:localhost"]);
        await Assert.That(snapshots[^1].All(e => ReferenceEquals(e.EventType, eventType))).IsTrue();
    }

    [Test]
    public async Task StickyEvents_ShouldYieldTheLiveEvents()
    {
        // Arrange
        await using SyncingClient alice = await SyncingClient.StartAsync(homeserver, "sticky");
        if (!await alice.Client.IsStickyEventsSupported())
        {
            Skip.Test("the homeserver doesn't support sticky events (MSC4354)");
        }
        using Room room = await alice.CreateRoomAsync();
        using CancellationTokenSource timeout = new(Poll.DefaultTimeout);
        List<StickyEvent[]> snapshots = [];

        // Act
        await foreach (StickyEvent[] events in room.WatchStickyEventsAsync(timeout.Token))
        {
            snapshots.Add(events);
            if (snapshots.Count == 1)
            {
                await room.SendStickyRaw(CustomType, """{"sticky_key":"a","value":"one"}""", 60_000);
                await room.SendStickyRaw(CustomType, """{"sticky_key":"b","value":"two"}""", 60_000);
                await room.SendStickyRaw(CustomType, """{"sticky_key":"a","value":"three"}""", 60_000);
            }
            if (events.Select(e => Value(e.EventJson, "content")).Order().SequenceEqual(["three", "two"]))
            {
                break;
            }
        }

        // Assert
        await Assert.That(snapshots[0]).IsEmpty();
        await Assert.That(snapshots[^1].Select(e => e.Key.Key)).IsEquivalentTo(["a", "b"]);
    }

    [Test]
    public async Task CustomToDeviceMessages_ShouldYieldMessagesOfTheTypes()
    {
        // Arrange
        await using SyncingClient alice = await SyncingClient.StartAsync(homeserver, "to-device-sender");
        await using SyncingClient bob = await SyncingClient.StartAsync(homeserver, "to-device-receiver");
        Dictionary<string, string[]> recipients = new() { [bob.Client.UserId()] = [bob.Client.DeviceId()] };
        using CancellationTokenSource timeout = new(Poll.DefaultTimeout);

        // Act
        Task<ToDeviceMessage> received = bob
            .Client.WatchCustomToDeviceMessagesAsync([CustomType], timeout.Token)
            .FirstAsync(timeout.Token)
            .AsTask();
        // the keys of Bob's device are uploaded with his first sync, Alice only knows them after querying them
        using Encryption encryption = alice.Client.Encryption();
        await Poll.UntilAsync(
            async () =>
            {
                using UserIdentity? _ = await encryption.UserIdentity(bob.Client.UserId(), fallbackToServer: true);
                SendToDeviceOutcome outcome = await alice.Client.SendEncryptedToDeviceMessage(
                    CustomType,
                    recipients,
                    """{"value":"secret"}"""
                );
                return outcome.Failures.Count == 0 ? (object)true : null;
            },
            "the message to be sent to Bob's device"
        );
        ToDeviceMessage message = await received;

        // Assert
        await Assert.That(message.EventType).IsEqualTo(CustomType);
        await Assert.That(message.SenderId).IsEqualTo(alice.Client.UserId());
        await Assert.That(Value(message.Content)).IsEqualTo("secret");
        await Assert.That(message.EncryptionInfo).IsNotNull();
    }

    private static string? Value(string json, string? property = null)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement element = property is null ? document.RootElement : document.RootElement.GetProperty(property);
        return element.GetProperty("value").GetString();
    }

    /// <summary>
    /// A client of a new user running the sync service.
    /// </summary>
    private sealed class SyncingClient(Client client, SyncService syncService) : IAsyncDisposable
    {
        public Client Client { get; } = client;

        public static async Task<SyncingClient> StartAsync(Homeserver homeserver, string prefix)
        {
            // the homeserver owns the client
            Client client = await homeserver.LoginAsync(await homeserver.CreateUserAsync(prefix));
            using SyncServiceBuilder builder = client.SyncService();
            SyncService syncService = await builder.Finish();
            await syncService.Start();
            return new SyncingClient(client, syncService);
        }

        public async Task<Room> CreateRoomAsync()
        {
            string roomId = await Client.CreateRoom(
                new CreateRoomParameters(
                    Name: "events",
                    IsEncrypted: false,
                    Visibility: new RoomVisibility.Private(),
                    Preset: RoomPreset.PrivateChat
                )
            );
            return await Poll.UntilAsync(() => Task.FromResult(Client.GetRoom(roomId)), "the room to be synced");
        }

        public async ValueTask DisposeAsync()
        {
            // a Stop right after Start, before the sync tasks ran once, is lost and never returns (matrix-rust-sdk
            // race, see SubscriptionTests), the skipped sticky events test stops right away
            try
            {
                await syncService.Stop().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                // the homeserver disposes the client
            }
            syncService.Dispose();
        }
    }
}

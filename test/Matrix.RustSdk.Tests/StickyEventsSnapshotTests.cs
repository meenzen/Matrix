using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Bindings.Base;
using Matrix.RustSdk.Subscriptions;
using TUnit.Assertions.Enums;

namespace Matrix.RustSdk.Tests;

/// <summary>
/// Folding the sticky event updates of the SDK into snapshots, without the SDK. The homeserver of the tests doesn't
/// support sticky events (MSC4354).
/// </summary>
public class StickyEventsSnapshotTests
{
    [Test]
    public async Task Changes_ShouldBeAppliedByKey()
    {
        // Arrange
        SubscriptionWriter<StickyEvent[]> writer = new(SubscriptionBuffer.All);
        RoomExtensions.StickyEventsSnapshotListener listener = new(writer);

        // Act
        listener.OnUpdate(new StickyEventsUpdate.Reset([Event("a", "$1"), Event("b", "$2")]));
        listener.OnUpdate(
            new StickyEventsUpdate.Changes(
                Added: [Event("c", "$3")],
                Updated: [Event("a", "$4")],
                Removed: [new StickyEventRemoval(Key("b"), RemovalReason.Expired)]
            )
        );

        // Assert
        await Assert.That(EventIds(writer)).IsEquivalentTo(["$1,$2", "$4,$3"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Reset_ShouldReplaceAllEvents()
    {
        // Arrange
        SubscriptionWriter<StickyEvent[]> writer = new(SubscriptionBuffer.All);
        RoomExtensions.StickyEventsSnapshotListener listener = new(writer);
        listener.OnUpdate(new StickyEventsUpdate.Reset([Event("a", "$1"), Event("b", "$2")]));

        // Act
        // the SDK resets after the listener fell behind
        listener.OnUpdate(new StickyEventsUpdate.Reset([Event("c", "$3")]));

        // Assert
        await Assert.That(EventIds(writer)).IsEquivalentTo(["$1,$2", "$3"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Snapshot_ShouldBeSortedByTypeKeyAndSender()
    {
        // Arrange
        SubscriptionWriter<StickyEvent[]> writer = new(SubscriptionBuffer.All);
        RoomExtensions.StickyEventsSnapshotListener listener = new(writer);

        // Act
        listener.OnUpdate(
            new StickyEventsUpdate.Reset([
                Event("a", "$1", sender: "@b:localhost"),
                Event("b", "$2", eventType: "m.a"),
                Event("a", "$3"),
                Event("a", "$4", sender: "@a:localhost"),
            ])
        );

        // Assert
        await Assert.That(EventIds(writer)).IsEquivalentTo(["$2,$4,$1,$3"]);
    }

    private static StickyKey Key(string stickyKey, string sender = "@c:localhost", string eventType = "m.b") =>
        new(sender, eventType, stickyKey);

    private static StickyEvent Event(
        string stickyKey,
        string eventId,
        string sender = "@c:localhost",
        string eventType = "m.b"
    ) => new(Key(stickyKey, sender, eventType), eventId, ExpiresAtMs: 0, EventJson: "{}", EncryptionInfo: null);

    private static string[] EventIds(SubscriptionWriter<StickyEvent[]> writer)
    {
        List<string> snapshots = [];
        while (writer.Reader.TryRead(out StickyEvent[]? events))
        {
            snapshots.Add(string.Join(',', events.Select(e => e.EventId)));
        }
        return [.. snapshots];
    }
}

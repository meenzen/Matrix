using Matrix.RustSdk.Examples.TuiClient.Chat;

namespace Matrix.RustSdk.Examples.TuiClient.Tests.Chat;

public class VisibleEntriesTests
{
    private static readonly TimelineEntry Marker = new("marker", EntryKind.ReadMarker, "new messages");

    [Test]
    public async Task VisibleEntries_ShouldKeepTheReadMarkerBeforeMessagesOfOthers()
    {
        // Arrange
        TimelineEntry[] entries =
        [
            new("1", EntryKind.Message, "read") { SenderId = "@alice:example.org" },
            Marker,
            new("2", EntryKind.Message, "unread") { SenderId = "@alice:example.org" },
        ];

        // Act
        IReadOnlyList<TimelineEntry> visible = TimelineRenderer.VisibleEntries(entries);

        // Assert
        await Assert.That(visible).Contains(Marker);
    }

    [Test]
    public async Task VisibleEntries_ShouldHideTheReadMarkerWithoutUnreadMessages()
    {
        // Arrange: only state events and own messages follow
        TimelineEntry[] entries =
        [
            Marker,
            new("1", EntryKind.Event, "alice joined"),
            new("2", EntryKind.Message, "mine") { IsOwn = true },
        ];

        // Act
        IReadOnlyList<TimelineEntry> visible = TimelineRenderer.VisibleEntries(entries);

        // Assert
        await Assert.That(visible.Select(e => e.Key)).IsEquivalentTo(["1", "2"]);
    }
}

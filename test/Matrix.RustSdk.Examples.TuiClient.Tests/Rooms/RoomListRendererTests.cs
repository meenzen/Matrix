using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Examples.TuiClient.Rendering;
using Matrix.RustSdk.Examples.TuiClient.Rooms;

namespace Matrix.RustSdk.Examples.TuiClient.Tests.Rooms;

public class RoomListRendererTests
{
    [Test]
    public async Task Layout_ShouldMarkTheKindUnreadAndOpenRooms()
    {
        // Arrange
        RoomSummary[] rooms =
        [
            new("!a", "General") { UnreadMessages = 3 },
            new("!b", "Alice")
            {
                IsDirect = true,
                UnreadMentions = 1,
                UnreadMessages = 1,
            },
            new("!c", "Party") { Membership = Membership.Invited },
            new("!d", "A room with a very long name that doesn't fit"),
        ];

        // Act
        IReadOnlyList<Row> rows = RoomListRenderer.Layout(rooms, 24, openRoomId: "!d");

        // Assert
        await Assert
            .That(rows.Select(r => r.Text))
            .IsEquivalentTo([
                " # General             3",
                " @ Alice              @1",
                " + Party                ",
                "▌# A room with a very l…",
            ]);
        await Assert.That(rows[0].Spans[3].Role).IsEqualTo(Role.Unread);
        await Assert.That(rows[1].Spans[3].Role).IsEqualTo(Role.Mention);
        await Assert.That(rows[2].Spans[3].Role).IsEqualTo(Role.Invite);
        await Assert.That(rows[3].Spans[3].Role).IsEqualTo(Role.Normal);
    }

    [Test]
    [Arguments(0ul, 0ul, false, "")]
    [Arguments(5ul, 0ul, false, "5")]
    [Arguments(150ul, 0ul, false, "99+")]
    [Arguments(5ul, 2ul, false, "@2")]
    [Arguments(0ul, 0ul, true, "!")]
    public async Task Badge_ShouldCountUnreadMessages(
        ulong messages,
        ulong mentions,
        bool markedUnread,
        string expected
    )
    {
        // Arrange
        RoomSummary room = new("!a", "Room")
        {
            UnreadMessages = messages,
            UnreadMentions = mentions,
            IsMarkedUnread = markedUnread,
        };

        // Act
        string badge = RoomListRenderer.Badge(room);

        // Assert
        await Assert.That(badge).IsEqualTo(expected);
    }
}

using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Examples.TuiClient.Tests.Support;
using Matrix.RustSdk.Testing;
using Terminal.Gui.Input;

namespace Matrix.RustSdk.Examples.TuiClient.Tests;

/// <summary>
/// Managing rooms in the TUI client: creating, inviting, joining, leaving, direct chats and the room list.
/// </summary>
[Category(Homeserver.Category)]
[ClassDataSource<Homeserver>(Shared = SharedType.PerTestSession)]
public class RoomTests(Homeserver homeserver)
{
    private static string Unique(string name) => $"{name} {Guid.NewGuid().ToString("N")[..6]}";

    [Test]
    public async Task CreateInviteAndLeave_ShouldManageTheRoom()
    {
        // Arrange
        await using TuiTester tui = await TuiTester.StartAsync(homeserver);
        await using Peer peer = await Peer.StartAsync(homeserver);
        string name = Unique("Book club");

        // Act: create
        await tui.CommandAsync($"create \"{name}\"");

        // Assert: the room is opened in insert mode
        await tui.WaitForTextAsync($"{name} │ encrypted");
        await tui.WaitForTextAsync("-- INSERT --");

        // Act: invite
        await tui.NormalModeAsync();
        await tui.CommandAsync($"invite {peer.User.UserId}");

        // Assert
        await tui.WaitForTextAsync($"Invited {peer.User.UserId}.");
        await peer.WaitForInviteAsync(name);

        // Act: the topic
        await tui.CommandAsync("topic Weekly books");

        // Assert
        await tui.WaitForTextAsync("│ Weekly books");

        // Act: leave
        await tui.CommandAsync("leave");
        await tui.WaitForTextAsync($"Leave {name}? (y/n)");
        await tui.PressAsync('y');

        // Assert
        await tui.WaitForTextAsync("Left the room.");
        await tui.WaitForTextGoneAsync(name);
    }

    [Test]
    public async Task Members_ShouldListTheMembers()
    {
        // Arrange
        await using TuiTester tui = await TuiTester.StartAsync(homeserver);
        await using Peer peer = await Peer.StartAsync(homeserver);
        string name = Unique("Members");
        await peer.CreateRoomAsync(name, invite: [tui.User.UserId]);
        await AcceptInviteAsync(tui, name);

        // Act
        await tui.NormalModeAsync();
        await tui.PressAsync('m');

        // Assert: the creator is the admin, the room has two members
        await tui.WaitForTextAsync($"Members of {name} (2)");
        await tui.WaitForTextAsync($"{peer.User.UserId}  (owner)");
        await tui.WaitForTextAsync(tui.User.UserId);

        // Act: Esc closes the page
        await tui.PressAsync(Key.Esc);

        // Assert
        await tui.WaitForTextGoneAsync("Members of");
    }

    [Test]
    public async Task Join_ShouldJoinAPublicRoomByItsAlias()
    {
        // Arrange
        await using TuiTester tui = await TuiTester.StartAsync(homeserver);
        await using Peer peer = await Peer.StartAsync(homeserver);
        string name = Unique("Lobby");
        string alias = $"lobby-{Guid.NewGuid().ToString("N")[..8]}";
        string roomId = await peer.CreateRoomAsync(name, isPublic: true, alias: alias);

        // Act
        await tui.CommandAsync($"join #{alias}:{Homeserver.ServerName}");

        // Assert
        await tui.WaitForTextAsync($"▌# {name}");
        await peer.WaitForMembershipAsync(
            roomId,
            tui.User.UserId,
            m => m is MembershipState.Join,
            "the TUI user to join"
        );
    }

    [Test]
    public async Task DeclineInvite_ShouldLeaveTheRoom()
    {
        // Arrange
        await using TuiTester tui = await TuiTester.StartAsync(homeserver);
        await using Peer peer = await Peer.StartAsync(homeserver);
        string name = Unique("Unwanted");
        string roomId = await peer.CreateRoomAsync(name, invite: [tui.User.UserId]);
        await tui.WaitForTextAsync($"+ {name}");

        // Act: the invite is the only room, Enter opens it, n declines
        await tui.PressAsync(Key.Enter);
        await tui.WaitForTextAsync($"{peer.User.Username}");
        await tui.WaitForTextAsync("Accept the invite");
        await tui.PressAsync('n');

        // Assert
        await tui.WaitForTextAsync("Declined the invite.");
        await tui.WaitForTextGoneAsync(name);
        await peer.WaitForMembershipAsync(
            roomId,
            tui.User.UserId,
            m => m is MembershipState.Leave,
            "the TUI user to decline"
        );
    }

    [Test]
    public async Task DirectMessage_ShouldCreateADirectChat()
    {
        // Arrange
        await using TuiTester tui = await TuiTester.StartAsync(homeserver);
        await using Peer peer = await Peer.StartAsync(homeserver);

        // Act
        await tui.CommandAsync($"dm {peer.User.UserId}");

        // Assert: the TUI client opens the room, the peer gets a direct invite
        await tui.WaitForTextAsync("-- INSERT --");
        await tui.WaitForTextAsync("▌@ ");
        string roomId = await peer.WaitForInviteAsync();
        using Room room = await peer.GetRoomAsync(roomId);
        await Assert.That(await room.IsDirect()).IsTrue();

        // Act: :dm again opens the same room instead of creating another one
        await tui.NormalModeAsync();
        await tui.CommandAsync($"dm {peer.User.UserId}");

        // Assert
        await tui.WaitForTextAsync("-- INSERT --");
        string screen = await tui.GetScreenAsync();
        await Assert
            .That(
                screen
                    .Split('\n')
                    .Count(l =>
                        l.Contains("@ ", StringComparison.Ordinal)
                        && l.Contains(peer.User.Username, StringComparison.Ordinal)
                    )
            )
            .IsLessThanOrEqualTo(1);
    }

    [Test]
    public async Task Filter_ShouldShowMatchingRoomsOnly()
    {
        // Arrange
        await using TuiTester tui = await TuiTester.StartAsync(homeserver);
        await using Peer peer = await Peer.StartAsync(homeserver);
        string suffix = Guid.NewGuid().ToString("N")[..6];
        await peer.CreateRoomAsync($"Alpha {suffix}", invite: [tui.User.UserId]);
        await peer.CreateRoomAsync($"Beta {suffix}", invite: [tui.User.UserId]);
        await tui.WaitForTextAsync($"Alpha {suffix}");
        await tui.WaitForTextAsync($"Beta {suffix}");

        // Act
        await tui.PressAsync('/');
        await tui.TypeAsync("Alpha");
        await tui.PressAsync(Key.Enter);

        // Assert
        await tui.WaitForTextGoneAsync($"Beta {suffix}");
        await tui.WaitForTextAsync($"Alpha {suffix}");
        await tui.WaitForTextAsync("Rooms /Alpha");

        // Act: Esc in the filter clears it
        await tui.PressAsync('/');
        await tui.PressAsync(Key.Esc);

        // Assert
        await tui.WaitForTextAsync($"Beta {suffix}");
    }

    [Test]
    public async Task UnreadMessages_ShouldBeCountedInTheRoomList()
    {
        // Arrange
        await using TuiTester tui = await TuiTester.StartAsync(homeserver);
        await using Peer peer = await Peer.StartAsync(homeserver);
        string name = Unique("Busy");
        string roomId = await peer.CreateRoomAsync(name, invite: [tui.User.UserId]);
        await AcceptInviteAsync(tui, name);
        // another room is open while the messages arrive
        await tui.NormalModeAsync();
        await tui.CommandAsync($"create \"{Unique("Quiet")}\"");
        await tui.WaitForTextAsync("-- INSERT --");

        // Act
        await peer.SendAsync(roomId, "first");
        await peer.SendAsync(roomId, "second");

        // Assert
        // the badge is at the end of the line of the room in the room list
        await tui.WaitForMatchAsync($@"{System.Text.RegularExpressions.Regex.Escape(name)}\s+2│");
        await tui.WaitForTextAsync("Rooms (1 unread)");

        // Act: U opens the next unread room, which marks it as read
        await tui.NormalModeAsync();
        await tui.PressAsync('U');

        // Assert
        await tui.WaitForTextAsync("second");
        await tui.WaitForTextGoneAsync("unread)");
    }

    /// <summary>
    /// Opens the invite to the room named <paramref name="name"/> and accepts it, the room list has the focus.
    /// </summary>
    internal static async Task AcceptInviteAsync(TuiTester tui, string name)
    {
        await tui.WaitForTextAsync($"+ {name}");
        await tui.CommandAsync($"search {name}");
        await tui.PressAsync(Key.Enter);
        await tui.WaitForTextAsync("Accept the invite");
        await tui.PressAsync('y');
        await tui.WaitForTextAsync($"Joined {name}.");
        // joining starts insert mode, the filter is cleared in normal mode and insert mode is restored
        await tui.NormalModeAsync();
        await tui.CommandAsync("search");
        await tui.PressAsync('i');
    }
}

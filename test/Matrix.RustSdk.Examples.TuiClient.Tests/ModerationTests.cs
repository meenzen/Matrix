using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Examples.TuiClient.Tests.Support;
using Matrix.RustSdk.Testing;
using Terminal.Gui.Input;

namespace Matrix.RustSdk.Examples.TuiClient.Tests;

/// <summary>
/// Moderating a room the TUI user created, the help page and moving between rooms with the keyboard.
/// </summary>
[Category(Homeserver.Category)]
[ClassDataSource<Homeserver>(Shared = SharedType.PerTestSession)]
public class ModerationTests(Homeserver homeserver)
{
    [Test]
    public async Task Moderation_ShouldRenameIgnoreKickAndBan()
    {
        // Arrange: the peer joined a room of the TUI user
        await using TuiTester tui = await TuiTester.StartAsync(homeserver);
        await using Peer peer = await Peer.StartAsync(homeserver);
        string name = $"Moderated {Guid.NewGuid().ToString("N")[..6]}";
        await tui.CommandAsync($"create \"{name}\"");
        await tui.WaitForTextAsync("-- INSERT --");
        await tui.NormalModeAsync();
        await tui.CommandAsync($"invite {peer.User.UserId}");
        string roomId = await peer.WaitForInviteAsync(name);
        using (Room room = await peer.GetRoomAsync(roomId))
        {
            await room.Join();
        }
        await tui.WaitForTextAsync($"{peer.User.Username} 💕 accepted the invite");

        // Act: rename
        string renamed = $"Renamed {Guid.NewGuid().ToString("N")[..6]}";
        await tui.CommandAsync($"name {renamed}");

        // Assert
        await tui.WaitForTextAsync($"renamed the room to {renamed}");

        // Act: ignore
        await tui.CommandAsync($"ignore {peer.User.UserId}");
        await tui.WaitForTextAsync($"Ignoring {peer.User.UserId}.");
        await tui.PressAsync('m');

        // Assert
        await tui.WaitForTextAsync($"{peer.User.UserId}  (ignored)");
        await tui.PressAsync('q');
        await tui.CommandAsync($"unignore {peer.User.UserId}");
        await tui.WaitForTextAsync($"No longer ignoring {peer.User.UserId}.");

        // Act: kick
        await tui.CommandAsync($"kick {peer.User.UserId} too loud");

        // Assert
        await tui.WaitForTextAsync("was removed: too loud");

        // Act: ban and unban
        await tui.CommandAsync($"ban {peer.User.UserId}");
        await tui.WaitForTextAsync("was banned");
        await tui.CommandAsync($"unban {peer.User.UserId}");

        // Assert
        await tui.WaitForTextAsync("was unbanned");
    }

    [Test]
    public async Task Help_ShouldListTheKeysUntilClosed()
    {
        // Arrange
        await using TuiTester tui = await TuiTester.StartAsync(homeserver);

        // Act
        await tui.PressAsync('?');

        // Assert
        await tui.WaitForTextAsync("┤Help├");
        await tui.WaitForTextAsync("next unread room");

        // Act: G scrolls to the end, q closes
        await tui.PressAsync('G');
        await tui.WaitForTextAsync(":search [text]");
        await tui.PressAsync('q');

        // Assert
        await tui.WaitForTextGoneAsync("┤Help├");
    }

    [Test]
    public async Task NextAndPreviousRoom_ShouldOpenTheRooms()
    {
        // Arrange: the newest room is at the top
        await using TuiTester tui = await TuiTester.StartAsync(homeserver);
        string suffix = Guid.NewGuid().ToString("N")[..6];
        await tui.CommandAsync($"create \"Older {suffix}\"");
        await tui.WaitForTextAsync($"┤Older {suffix}");
        await tui.NormalModeAsync();
        await tui.CommandAsync($"create \"Newer {suffix}\"");
        await tui.WaitForTextAsync($"┤Newer {suffix}");
        await tui.NormalModeAsync();
        await tui.WaitForMatchAsync($@"Newer {suffix}\s+││[\s\S]*Older {suffix}");
        await tui.PressAsync('h');
        await tui.TypeAsync("gg");

        // Act
        await tui.PressAsync('J');

        // Assert
        await tui.WaitForTextAsync($"┤Older {suffix}");

        // Act
        await tui.PressAsync(Key.Esc);
        await tui.PressAsync('K');

        // Assert
        await tui.WaitForTextAsync($"┤Newer {suffix}");

        // Act: a double click on the second line of the room list opens the room there
        await tui.PressAsync(Key.Esc);
        await tui.DoubleClickAsync(6, 3);

        // Assert
        await tui.WaitForTextAsync($"┤Older {suffix}");
    }
}

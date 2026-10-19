using Matrix.RustSdk.Examples.TuiClient.Tests.Support;
using Matrix.RustSdk.Testing;

namespace Matrix.RustSdk.Examples.TuiClient.Tests;

/// <summary>
/// Desktop notifications of the TUI client for direct messages, except for the room the user is reading.
/// </summary>
[Category(Homeserver.Category)]
[ClassDataSource<Homeserver>(Shared = SharedType.PerTestSession)]
public class NotificationTests(Homeserver homeserver)
{
    [Test]
    public async Task DirectMessages_ShouldNotifyUnlessTheRoomIsOpen()
    {
        // Arrange: a direct chat, the room is open
        await using TuiTester tui = await TuiTester.StartAsync(homeserver);
        await using Peer peer = await Peer.StartAsync(homeserver);
        string name = $"Direct {Guid.NewGuid().ToString("N")[..6]}";
        string roomId = await peer.CreateRoomAsync(name, invite: [tui.User.UserId], isDirect: true);
        await RoomTests.AcceptInviteAsync(tui, name);

        // Act: a message while the room is open
        await peer.SendAsync(roomId, "while reading");
        await tui.WaitForTextAsync("while reading");

        // Act: a message while another room is open
        await tui.NormalModeAsync();
        await tui.CommandAsync($"create \"Elsewhere {Guid.NewGuid().ToString("N")[..6]}\"");
        await tui.WaitForTextAsync("-- INSERT --");
        await peer.SendAsync(roomId, "while away");

        // Assert
        await tui.Notifier.WaitForAsync(n => n.Body == "while away", "the notification");
        await Assert.That(tui.Notifier.Notifications.Any(n => n.Body == "while reading")).IsFalse();

        // Act: notifications can be turned off
        await tui.NormalModeAsync();
        await tui.CommandAsync("notifications off");
        await tui.WaitForTextAsync("notifications off");
        await peer.SendAsync(roomId, "while muted");
        await peer.SendAsync(roomId, "after muting");
        // the room list counts the three messages that weren't read
        await tui.WaitForMatchAsync($@"{name}\s+3│");

        // Assert
        await Assert.That(tui.Notifier.Notifications.Any(n => n.Body is "while muted" or "after muting")).IsFalse();
    }
}

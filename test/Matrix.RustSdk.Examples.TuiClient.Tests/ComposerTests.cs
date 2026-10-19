using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Examples.TuiClient.Chat;
using Matrix.RustSdk.Examples.TuiClient.Tests.Support;
using Matrix.RustSdk.Testing;
using Terminal.Gui.Input;

namespace Matrix.RustSdk.Examples.TuiClient.Tests;

/// <summary>
/// Writing in the TUI client: drafts, completion, editing keys, links, reactions, and questions that wait until the
/// user stops writing.
/// </summary>
[Category(Homeserver.Category)]
[ClassDataSource<Homeserver>(Shared = SharedType.PerTestSession)]
public class ComposerTests(Homeserver homeserver)
{
    [Test]
    public async Task Draft_ShouldStayInItsRoom()
    {
        // Arrange
        await using TuiTester tui = await TuiTester.StartAsync(homeserver);
        string suffix = Guid.NewGuid().ToString("N")[..6];
        await tui.CommandAsync($"create \"First {suffix}\"");
        await tui.WaitForTextAsync("-- INSERT --");

        // Act: C-w deletes the last word
        await tui.TypeAsync("draft for the first room oops");
        await tui.PressAsync(Key.W.WithCtrl);
        await tui.WaitForTextAsync("draft for the first room ");
        await tui.WaitForTextGoneAsync("oops");

        // Act: another room has its own composer
        await tui.NormalModeAsync();
        await tui.CommandAsync($"create \"Second {suffix}\"");
        await tui.WaitForTextAsync($"┤Second {suffix}");

        // Assert
        await tui.WaitForTextGoneAsync("draft for the first room");

        // Act: back in the first room
        await tui.NormalModeAsync();
        await tui.PressAsync('J');

        // Assert
        await tui.WaitForTextAsync($"┤First {suffix}");
        await tui.WaitForTextAsync("draft for the first room");
    }

    [Test]
    public async Task LinksReactionsAndNames_ShouldWork()
    {
        // Arrange
        await using TuiTester tui = await TuiTester.StartAsync(homeserver);
        await using Peer peer = await Peer.StartAsync(homeserver);
        string name = $"Links {Guid.NewGuid().ToString("N")[..6]}";
        string roomId = await peer.CreateRoomAsync(name, invite: [tui.User.UserId]);
        LiveList<TimelineEntry> peerTimeline = await peer.WatchAsync(roomId);
        await RoomTests.AcceptInviteAsync(tui, name);
        await peer.SendAsync(roomId, "have a look at https://example.org/page.");
        await tui.WaitForTextAsync("have a look at");

        // Act: o opens the link of the selected message
        await tui.NormalModeAsync();
        await tui.PressAsync('G');
        await tui.PressAsync('o');

        // Assert
        await Poll.UntilAsync(
            () => Task.FromResult(tui.OpenedFiles.Contains("https://example.org/page")),
            "the link to open"
        );

        // Act: a shortcode reacts with the emoji
        await tui.CommandAsync("react +1");

        // Assert
        await Peer.WaitForEntryAsync(
            peerTimeline,
            e => e.Body == "have a look at https://example.org/page." && e.Reactions.Any(r => r.Key == "👍"),
            "the reaction"
        );

        // Act: Tab completes the name of a member
        await tui.PressAsync('i');
        await tui.TypeAsync(peer.User.Username[..6]);
        await tui.PressAsync(Key.Tab);

        // Assert: the display name of the peer is followed by a colon at the start of a message
        await tui.WaitForMatchAsync($@"{peer.User.Username}\S* ?\S*: ");
    }

    [Test]
    public async Task VerificationRequest_ShouldWaitUntilTheUserStopsWriting()
    {
        // Arrange: writing a message when another session asks to verify
        await using TuiTester tui = await TuiTester.StartAsync(homeserver);
        await tui.WaitForTextAsync("verified backup:on");
        await EncryptionTests.WaitUntilVerificationIsReadyAsync(tui);
        await tui.CommandAsync($"create \"Writing {Guid.NewGuid().ToString("N")[..6]}\"");
        await tui.WaitForTextAsync("-- INSERT --");
        await tui.TypeAsync("hello");
        Client device = await homeserver.LoginAsync(tui.User);
        await using EncryptionTests.SyncingDevice syncing = await EncryptionTests.SyncingDevice.StartAsync(device);
        using SessionVerificationController controller = await EncryptionTests.GetControllerAsync(device);
        EncryptionTests.RecordingVerificationDelegate recorder = new();
        controller.SetDelegate(recorder);

        // Act
        await controller.RequestDeviceVerification();
        await tui.WaitForTextAsync("a question waits for normal mode");
        await tui.TypeAsync(" yes");

        // Assert: the typed y went into the message, not to the question
        await tui.WaitForTextAsync("hello yes");

        // Act: in normal mode the question can be answered, n declines
        await tui.PressAsync(Key.Esc);
        await tui.WaitForTextAsync("wants to verify. Accept? (y/n)");
        await tui.PressAsync('n');

        // Assert: the other session is told
        await Poll.UntilAsync(
            () => Task.FromResult(recorder.Events.Contains("cancelled")),
            "the other session to see the cancellation"
        );
    }
}

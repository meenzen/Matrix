using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Examples.TuiClient.Chat;
using Matrix.RustSdk.Examples.TuiClient.Tests.Support;
using Matrix.RustSdk.Testing;
using Terminal.Gui.Input;

namespace Matrix.RustSdk.Examples.TuiClient.Tests;

/// <summary>
/// Writing and acting on messages in the TUI client. The tests build on each other in one room: the peer checks what
/// the TUI client sent with its own timeline.
/// </summary>
[Category(Homeserver.Category)]
[ClassDataSource<Homeserver>(Shared = SharedType.PerTestSession)]
public class MessageTests(Homeserver homeserver)
{
    private static readonly string RoomName = $"Messages {Guid.NewGuid().ToString("N")[..6]}";
    private const int HistoryMessages = 70;

    private static TuiTester? _tui;
    private static Peer? _peer;
    private static string? _roomId;
    private static LiveList<TimelineEntry>? _peerTimeline;

    private static TuiTester Tui => _tui ?? throw new InvalidOperationException("Setup didn't run.");

    private static Peer Peer => _peer ?? throw new InvalidOperationException("Setup didn't run.");

    private static string RoomId => _roomId ?? throw new InvalidOperationException("Setup didn't run.");

    private static LiveList<TimelineEntry> PeerTimeline =>
        _peerTimeline ?? throw new InvalidOperationException("Setup didn't run.");

    [Test]
    public async Task Open_ShouldShowTheRecentHistory()
    {
        // Arrange: the room has more history than the client loads at first
        _tui = await TuiTester.StartAsync(homeserver);
        _peer = await Peer.StartAsync(homeserver);
        _roomId = await Peer.CreateRoomAsync(RoomName, invite: [Tui.User.UserId]);
        Timeline timeline = await Peer.TimelineAsync(RoomId);
        for (int i = 1; i <= HistoryMessages; i++)
        {
            await timeline.SendTextAsync($"history {i}");
        }
        _peerTimeline = await Peer.WatchAsync(RoomId);
        await Peer.WaitForEntryAsync(
            PeerTimeline,
            e => e.Body == $"history {HistoryMessages}" && e.Status == SendStatus.Sent,
            "the history to be sent"
        );

        // Act
        await RoomTests.AcceptInviteAsync(Tui, RoomName);

        // Assert
        await Tui.WaitForTextAsync($"history {HistoryMessages}");
    }

    [Test]
    [DependsOn(nameof(Open_ShouldShowTheRecentHistory))]
    public async Task Send_ShouldSendTheComposerText()
    {
        // Act: joining the room started insert mode
        await Tui.TypeAsync("hello **world**");
        await Tui.PressAsync(Key.Enter);

        // Assert: markdown is sent as HTML, the body keeps the markdown
        await Tui.WaitForTextAsync("hello **world**");
        await Peer.WaitForEntryAsync(PeerTimeline, e => e.Body == "hello **world**", "the message");
    }

    [Test]
    [DependsOn(nameof(Send_ShouldSendTheComposerText))]
    public async Task NewLine_ShouldSendSeveralLines()
    {
        // Act
        await Tui.TypeAsync("first line");
        await Tui.PressAsync(Key.Enter.WithAlt);
        await Tui.TypeAsync("second line");
        await Tui.PressAsync(Key.Enter);

        // Assert
        await Peer.WaitForEntryAsync(
            PeerTimeline,
            e => e.Body == "first line\nsecond line",
            "the message with two lines"
        );
        await Tui.WaitForTextAsync("second line");
    }

    [Test]
    [DependsOn(nameof(NewLine_ShouldSendSeveralLines))]
    public async Task Reply_ShouldReferToTheSelectedMessage()
    {
        // Arrange
        Timeline timeline = await Peer.TimelineAsync(RoomId);
        await timeline.SendTextAsync("a question?");
        await Tui.WaitForTextAsync("a question?");
        TimelineEntry question = await Peer.WaitForEntryAsync(
            PeerTimeline,
            e => e is { Body: "a question?", EventId: not null },
            "the question"
        );

        // Act: normal mode selects the newest message, r replies to it
        await Tui.PressAsync(Key.Esc);
        await Tui.PressAsync('r');
        await Tui.WaitForTextAsync("replying to");
        await Tui.TypeAsync("an answer");
        await Tui.PressAsync(Key.Enter);

        // Assert
        TimelineEntry answer = await Peer.WaitForEntryAsync(PeerTimeline, e => e.Body == "an answer", "the answer");
        await Assert.That(answer.ReplyTo?.EventId).IsEqualTo(question.EventId);
        await Tui.WaitForTextAsync("↳ ");
        await Tui.WaitForTextGoneAsync("replying to");
    }

    [Test]
    [DependsOn(nameof(Reply_ShouldReferToTheSelectedMessage))]
    public async Task Edit_ShouldReplaceTheText()
    {
        // Act: the answer is the newest message, e puts its text into the composer
        await Tui.PressAsync(Key.Esc);
        await Tui.PressAsync('e');
        await Tui.WaitForTextAsync("editing:");
        await Tui.TypeAsync(" (corrected)");
        await Tui.PressAsync(Key.Enter);

        // Assert
        await Peer.WaitForEntryAsync(
            PeerTimeline,
            e => e is { Body: "an answer (corrected)", IsEdited: true },
            "the edit"
        );
        await Tui.WaitForTextAsync("an answer (corrected) (edited)");
    }

    [Test]
    [DependsOn(nameof(Edit_ShouldReplaceTheText))]
    public async Task React_ShouldToggleAReaction()
    {
        // Act: the question is one above the answer
        await Tui.PressAsync(Key.Esc);
        await Tui.PressAsync('k');
        await Tui.PressAsync('+');
        await Tui.TypeAsync("❤");
        await Tui.PressAsync(Key.Enter);

        // Assert
        await Peer.WaitForEntryAsync(
            PeerTimeline,
            e => e.Body == "a question?" && e.Reactions.Any(r => r.Key == "❤"),
            "the reaction"
        );
        await Tui.WaitForTextAsync("Reacted with ❤.");
    }

    [Test]
    [DependsOn(nameof(React_ShouldToggleAReaction))]
    public async Task Delete_ShouldRedactTheMessage()
    {
        // Act: back to the answer, dd deletes it after asking
        await Tui.PressAsync('G');
        await Tui.TypeAsync("dd");
        await Tui.WaitForTextAsync("Delete \"an answer (corrected)\"? (y/n)");
        await Tui.PressAsync('y');

        // Assert
        await Peer.WaitForEntryAsync(
            PeerTimeline,
            e => e.Kind == EntryKind.Redacted && e.SenderId == Tui.User.UserId,
            "the redaction"
        );
        await Tui.WaitForTextAsync("(deleted)");
    }

    [Test]
    [DependsOn(nameof(Delete_ShouldRedactTheMessage))]
    public async Task Emote_ShouldSendAnEmote()
    {
        // Act
        await Tui.CommandAsync("me waves");

        // Assert
        await Peer.WaitForEntryAsync(PeerTimeline, e => e is { Kind: EntryKind.Emote, Body: "waves" }, "the emote");
        await Tui.WaitForTextAsync(" waves");
    }

    [Test]
    [DependsOn(nameof(Emote_ShouldSendAnEmote))]
    public async Task Typing_ShouldShowWhoIsTyping()
    {
        // Act
        using Room room = await Peer.GetRoomAsync(RoomId);
        await room.TypingNotice(true);

        // Assert
        await Tui.WaitForTextAsync("is typing…");
        await room.TypingNotice(false);
        await Tui.WaitForTextGoneAsync("is typing…");
    }

    [Test]
    [DependsOn(nameof(Typing_ShouldShowWhoIsTyping))]
    public async Task ReadReceipts_ShouldShowWhoReadAMessage()
    {
        // Arrange
        await Tui.PressAsync('i');
        await Tui.TypeAsync("did you read this?");
        await Tui.PressAsync(Key.Enter);
        await Peer.WaitForEntryAsync(
            PeerTimeline,
            e => e is { Body: "did you read this?", Status: SendStatus.Sent },
            "the message"
        );

        // Act
        Timeline timeline = await Peer.TimelineAsync(RoomId);
        await timeline.MarkAsRead(ReceiptType.Read);

        // Assert
        await Tui.WaitForTextAsync($"✓ {Peer.User.Username}");
    }

    [Test]
    [DependsOn(nameof(ReadReceipts_ShouldShowWhoReadAMessage))]
    public async Task ScrollingUp_ShouldLoadOlderMessages()
    {
        // Act: gg jumps to the oldest loaded message, which loads more until the start of the room
        await Tui.PressAsync(Key.Esc);
        await Poll.UntilAsync(
            async () =>
            {
                await Tui.TypeAsync("gg");
                return (await Tui.GetScreenAsync()).Contains("created the room", StringComparison.Ordinal);
            },
            "the start of the room"
        );

        // Assert
        await Tui.WaitForTextAsync("history 1");
    }

    [After(Class)]
    public static async Task StopAsync()
    {
        if (_tui is not null)
        {
            await _tui.DisposeAsync();
        }
        if (_peer is not null)
        {
            await _peer.DisposeAsync();
        }
    }
}

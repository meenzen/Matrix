using System.Collections.Concurrent;
using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Testing;
using Terminal.Gui.Input;

namespace Matrix.RustSdk.Examples.TuiClient.Tests;

/// <summary>
/// End-to-end tests of the TUI client against a tuwunel homeserver. The tests build on each other with
/// <see cref="DependsOnAttribute"/> like a user session: log in, get invited to a room, open it and chat with another
/// user, quit.
/// </summary>
[Category(Homeserver.Category)]
[ClassDataSource<Homeserver>(Shared = SharedType.PerTestSession)]
public class TuiClientTests(Homeserver homeserver)
{
    private static readonly string RoomName = $"TUI test room {Guid.NewGuid().ToString("N")[..8]}";

    private static TuiClientRunner? _tui;
    private static TestUser? _tuiUser;

    // the other user chats with the TUI client, it uses the SDK directly
    private static TestUser? _otherUser;
    private static string? _roomId;
    private static Timeline? _otherTimeline;
    private static TaskHandle? _otherTimelineHandle;
    private static SyncService? _otherSyncService;
    private static readonly MessageCollector OtherMessages = new();

    private static TuiClientRunner Tui =>
        _tui ?? throw new InvalidOperationException($"{nameof(Login_ShouldShowTheChatWindow)} didn't run.");

    private static TestUser TuiUser =>
        _tuiUser ?? throw new InvalidOperationException($"{nameof(Login_ShouldShowTheChatWindow)} didn't run.");

    private static TestUser OtherUser =>
        _otherUser ?? throw new InvalidOperationException($"{nameof(Login_ShouldShowTheChatWindow)} didn't run.");

    private static Timeline OtherTimeline =>
        _otherTimeline ?? throw new InvalidOperationException($"{nameof(RoomList_ShouldShowTheInvite)} didn't run.");

    [Test]
    public async Task Login_ShouldShowTheChatWindow()
    {
        // Arrange
        // the first user of a tuwunel server is its admin and is in the admin room, registering the other user first
        // keeps the room list of the TUI user empty until the test room shows up
        _otherUser = await homeserver.CreateUserAsync("other");
        _tuiUser = await homeserver.CreateUserAsync("tui");
        // the homeserver is passed like --homeserver, username and password are typed into the login form
        _tui = await TuiClientRunner.StartAsync(new LoginOptions(homeserver.Url, Username: null, Password: null));
        await Tui.WaitForTextAsync("Username:");

        // Act
        await Tui.TypeAsync(TuiUser.Username);
        await Tui.PressAsync(Key.Tab);
        await Tui.TypeAsync(TuiUser.Password);
        await Tui.PressAsync(Key.Enter);

        // Assert
        await Tui.WaitForTextAsync($"Matrix TUI client - {TuiUser.UserId}");
        await Tui.WaitForTextAsync("Sync: running");
    }

    [Test]
    [DependsOn(nameof(Login_ShouldShowTheChatWindow))]
    public async Task RoomList_ShouldShowTheInvite()
    {
        // Arrange
        Client otherClient = await homeserver.LoginAsync(OtherUser);

        // Act
        _roomId = await otherClient.CreateRoom(
            new CreateRoomParameters(
                Name: RoomName,
                IsEncrypted: false,
                Visibility: new RoomVisibility.Private(),
                Preset: RoomPreset.PrivateChat,
                Invite: [TuiUser.UserId]
            )
        );

        // Assert
        await Tui.WaitForTextAsync($"{RoomName} (invite)");

        // the other user syncs and watches the room from now on, the last test checks what it receives
        _otherSyncService = await otherClient.SyncService().Finish();
        await _otherSyncService.Start();
        Room room = otherClient.GetRoom(_roomId) ?? throw new InvalidOperationException("The room is unknown.");
        _otherTimeline = await room.Timeline();
        _otherTimelineHandle = await _otherTimeline.AddListener(OtherMessages);
    }

    [Test]
    [DependsOn(nameof(RoomList_ShouldShowTheInvite))]
    public async Task OpenRoom_ShouldShowMessagesOfOtherUsers()
    {
        // Arrange
        const string message = "Hello from the other side";
        using RoomMessageEventContentWithoutRelation content = MatrixSdkFfiMethods.MessageEventContentFromMarkdown(
            message
        );
        using SendHandle sendHandle = await OtherTimeline.Send(content);

        // Act
        // the room list has the focus and the invite is the only room, Enter joins and opens it
        await Tui.PressAsync(Key.Enter);

        // Assert
        await Tui.WaitForTextAsync($"> {message}");
    }

    [Test]
    [DependsOn(nameof(OpenRoom_ShouldShowMessagesOfOtherUsers))]
    public async Task SendMessage_ShouldBeReceivedByOtherUsers()
    {
        // Arrange
        const string message = "Hello from the terminal";

        // Act
        // the composer has the focus after opening a room
        await Tui.TypeAsync(message);
        await Tui.PressAsync(Key.Enter);

        // Assert
        // tuwunel appends an emoji to the display names of new users, only the message itself is checked
        await Tui.WaitForTextAsync($"> {message}");
        await OtherMessages.WaitForAsync(message, TuiClientRunner.Timeout);
    }

    [Test]
    [DependsOn(nameof(SendMessage_ShouldBeReceivedByOtherUsers))]
    public async Task Esc_ShouldQuit()
    {
        // Act
        await Tui.PressAsync(Key.Esc);

        // Assert
        await Tui.Completion.WaitAsync(TuiClientRunner.Timeout);
        await Assert.That(Tui.Completion.IsCompletedSuccessfully).IsTrue();
    }

    [After(Class)]
    public static async Task StopAsync()
    {
        if (_tui is not null)
        {
            await _tui.DisposeAsync();
        }
        _otherTimelineHandle?.Cancel();
        _otherTimelineHandle?.Dispose();
        _otherTimeline?.Dispose();
        if (_otherSyncService is not null)
        {
            await _otherSyncService.Stop();
            _otherSyncService.Dispose();
        }
    }

    /// <summary>
    /// Collects the bodies of the messages in a timeline.
    /// </summary>
    private sealed class MessageCollector : TimelineListener
    {
        private readonly ConcurrentDictionary<string, bool> _bodies = new();

        public void OnUpdate(TimelineDiff[] diff)
        {
            IEnumerable<TimelineItem> items = diff.SelectMany(d =>
                d switch
                {
                    TimelineDiff.Append append => append.Values,
                    TimelineDiff.Reset reset => reset.Values,
                    TimelineDiff.PushBack pushBack => [pushBack.Value],
                    TimelineDiff.PushFront pushFront => [pushFront.Value],
                    TimelineDiff.Insert insert => [insert.Value],
                    TimelineDiff.Set set => [set.Value],
                    _ => [],
                }
            );
            foreach (TimelineItem item in items)
            {
                using EventTimelineItem? eventItem = item.AsEvent();
                // remote events only, the other user's own local echoes don't count as received
                if (
                    eventItem is
                    {
                        IsRemote: true,
                        Content: TimelineItemContent.MsgLike { Content.Kind: MsgLikeKind.Message message }
                    }
                )
                {
                    _bodies[message.Content.Body] = true;
                }
            }
        }

        public async Task WaitForAsync(string body, TimeSpan timeout)
        {
            using CancellationTokenSource cancellation = new(timeout);
            while (!_bodies.ContainsKey(body))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellation.Token);
            }
        }
    }
}

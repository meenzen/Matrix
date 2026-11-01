using System.Collections.Concurrent;
using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Testing;
using Terminal.Gui.Input;

namespace Matrix.RustSdk.Examples.TuiClient.Tests;

/// <summary>
/// End-to-end tests of the TUI client against a tuwunel homeserver. The tests build on each other with
/// <see cref="DependsOnAttribute"/> like a user session: log in, get invited to a room, open it and chat with another
/// user, quit, restore the session at the next start and log out.
/// </summary>
[Category(Homeserver.Category)]
[ClassDataSource<Homeserver>(Shared = SharedType.PerTestSession)]
public class TuiClientTests(Homeserver homeserver)
{
    private static readonly string RoomName = $"TUI test room {Guid.NewGuid().ToString("N")[..8]}";

    private static readonly TemporaryDirectory DataDirectory = new();
    private static TuiClientRunner? _tui;
    private static TestUser? _tuiUser;

    // the other user chats with the TUI client, it uses the SDK directly
    private static TestUser? _otherUser;
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
        _tuiUser = await homeserver.CreateUserAsync("tui");
        _otherUser = await homeserver.CreateUserAsync("other");
        // the homeserver is passed like --homeserver, username and password are typed into the login form
        _tui = await TuiClientRunner.StartAsync(
            new LoginOptions(homeserver.Url, Username: null, Password: null, DataDirectory.Path)
        );
        await Tui.WaitForTextAsync("Username:");

        // Act
        await Tui.TypeAsync(TuiUser.Username);
        await Tui.PressAsync(Key.Tab);
        await Tui.TypeAsync(TuiUser.Password);
        await Tui.PressAsync(Key.Enter);

        // Assert
        await Tui.WaitForTextAsync($"Matrix TUI client - {TuiUser.UserId}");
        await Tui.WaitForTextAsync("sync: running");
    }

    [Test]
    [DependsOn(nameof(Login_ShouldShowTheChatWindow))]
    public async Task RoomList_ShouldShowTheInvite()
    {
        // Arrange
        Client otherClient = await homeserver.LoginAsync(OtherUser);

        // Act
        string roomId = await otherClient.CreateRoom(
            new CreateRoomParameters(
                Name: RoomName,
                IsEncrypted: false,
                Visibility: new RoomVisibility.Private(),
                Preset: RoomPreset.PrivateChat,
                Invite: [TuiUser.UserId]
            )
        );

        // Assert
        await Tui.WaitForTextAsync($"+ {RoomName}");

        // the other user syncs and watches the room from now on, the last test checks what it receives
        _otherSyncService = await otherClient.SyncService().Finish();
        await _otherSyncService.Start();
        Room room = otherClient.GetRoom(roomId) ?? throw new InvalidOperationException("The room is unknown.");
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
        // the room list has the focus and the invite is the only room, Enter opens it and y accepts the invite
        await Tui.PressAsync(Key.Enter);
        await Tui.WaitForTextAsync("Accept the invite");
        await Tui.PressAsync(new Key('y'));

        // Assert
        await Tui.WaitForTextAsync(message);
    }

    [Test]
    [DependsOn(nameof(OpenRoom_ShouldShowMessagesOfOtherUsers))]
    public async Task SendMessage_ShouldBeReceivedByOtherUsers()
    {
        // Arrange
        const string message = "Hello from the terminal";

        // Act
        // joining the room starts insert mode
        await Tui.WaitForTextAsync("-- INSERT --");
        await Tui.TypeAsync(message);
        await Tui.PressAsync(Key.Enter);

        // Assert
        await Tui.WaitForTextAsync(message);
        await OtherMessages.WaitForAsync(message);
    }

    [Test]
    [DependsOn(nameof(SendMessage_ShouldBeReceivedByOtherUsers))]
    public async Task Quit_ShouldExit()
    {
        // Act: Esc only leaves insert mode
        await Tui.PressAsync(Key.Esc);
        await Tui.CommandAsync("q");

        // Assert
        await Tui.Completion.WaitAsync(Poll.DefaultTimeout);
        await Assert.That(Tui.Completion.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    [DependsOn(nameof(Quit_ShouldExit))]
    public async Task Restart_ShouldRestoreTheSession()
    {
        // Arrange
        await Tui.DisposeAsync();

        // Act: without credentials, only the stored session can log in
        _tui = await TuiClientRunner.StartAsync(
            new LoginOptions(Homeserver: null, Username: null, Password: null, DataDirectory.Path)
        );

        // Assert
        await Tui.WaitForTextAsync($"Matrix TUI client - {TuiUser.UserId}");
        await Tui.WaitForTextAsync(RoomName);
    }

    [Test]
    [DependsOn(nameof(Restart_ShouldRestoreTheSession))]
    public async Task Logout_ShouldDeleteTheSession()
    {
        // Act
        await Tui.CommandAsync("logout");
        await Tui.WaitForTextAsync("Log out?");
        await Tui.PressAsync(new Key('y'));

        // Assert: back to the login form, the session is gone
        await Tui.WaitForTextAsync("Username:");
        await Assert.That(File.Exists(Path.Join(DataDirectory.Path, "session.json"))).IsFalse();
    }

    [After(Class)]
    public static async Task StopAsync()
    {
        if (_tui is not null)
        {
            await _tui.DisposeAsync();
        }
        DataDirectory.Dispose();
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

        public Task WaitForAsync(string body) =>
            Poll.UntilAsync(() => Task.FromResult(_bodies.ContainsKey(body)), $"the message \"{body}\"");
    }
}

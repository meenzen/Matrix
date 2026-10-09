using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Examples.TuiClient.Chat;
using Matrix.RustSdk.Examples.TuiClient.Input;
using Matrix.RustSdk.Examples.TuiClient.Notifications;
using Matrix.RustSdk.Examples.TuiClient.Rendering;
using Matrix.RustSdk.Examples.TuiClient.Rooms;
using Matrix.RustSdk.Examples.TuiClient.Security;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Matrix.RustSdk.Examples.TuiClient;

/// <summary>
/// The main window: the room list on the left, the timeline of the opened room (or a page like the help or the member
/// list) and the composer on the right, the status line or the command line at the bottom. Keys are handled like in
/// vim, see <see cref="VimKeymap"/>: this partial class lays out the window and shows the state of the session,
/// <c>ChatWindow.Input.cs</c> handles keys and <c>ChatWindow.Commands.cs</c> the <c>:</c> commands.
/// </summary>
/// <remarks>
/// Views may only be changed on the main loop of Terminal.Gui. The session reports changes on other threads, so every
/// update is moved to the main loop with <see cref="Post"/>, which renders the latest state. Actions started on the
/// main loop continue on it after awaiting, Terminal.Gui installs a <see cref="SynchronizationContext"/>.
/// </remarks>
public sealed partial class ChatWindow : Window, IAsyncDisposable
{
    private const int MaxComposerLines = 6;

    private readonly IApplication _app;
    private readonly MatrixSession _session;
    private readonly ClientSettings _settings;
    private readonly DateTimeOffset _startedAt;
    private readonly FrameView _roomsFrame;
    private readonly RowsView _roomsView;
    private readonly FrameView _mainFrame;
    private readonly RowsView _timelineView;
    private readonly RowsView _pageView;
    private readonly RuleView _rule;
    // TextView is obsolete in favor of a separate editor package, it is still the multi-line text field of Terminal.Gui
#pragma warning disable CS0618
    private readonly TextView _composer;
#pragma warning restore CS0618
    private readonly Label _status;
    private readonly Label _commandPrefix;
    private readonly TextField _commandLine;

    private IReadOnlyList<RoomSummary> _rooms = [];
    private IReadOnlyList<TimelineEntry> _entries = [];
    private OpenedRoom? _room;
    private int _roomGeneration;
    private Page? _page;
    private string? _lastReadKey;
    private bool _disposed;
    private bool _notificationsEnabled;
    private int _composerLines = 1;

    // written by the session's threads, the status line shows the latest value
    private volatile string _syncState = "starting";

    public ChatWindow(IApplication app, MatrixSession session, ClientSettings? settings = null)
    {
        _app = app;
        _session = session;
        _settings = settings ?? new ClientSettings();
        _notificationsEnabled = _settings.NotificationsEnabled;
        _startedAt = _settings.Clock.GetUtcNow();
        Title = $"Matrix TUI client - {session.UserId}";

        _roomsFrame = new FrameView
        {
            Title = "Rooms",
            Width = Dim.Percent(25),
            Height = Dim.Fill(1),
        };
        _roomsView = new RowsView
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(),
            Placeholder = "No rooms yet, :join or :create one.",
        };
        _roomsFrame.Add(_roomsView);

        _mainFrame = new FrameView
        {
            Title = "Timeline",
            X = Pos.Right(_roomsFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill(1),
        };
        _timelineView = new RowsView
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            FollowsEnd = true,
            ShowsSelectionWithoutFocus = false,
            Placeholder = "Select a room and press Enter to open it, ? shows the keys.",
        };
        _timelineView.SelectionChanged += (_, _) => OnTimelineSelectionChanged();
        _pageView = new RowsView
        {
            Width = Dim.Fill(),
            Height = Dim.Fill(2),
            Visible = false,
            ShowsSelectionWithoutFocus = false,
        };
        _rule = new RuleView { Y = Pos.AnchorEnd(2), Width = Dim.Fill() };
#pragma warning disable CS0618 // see _composer
        _composer = new TextView
#pragma warning restore CS0618
        {
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Height = 1,
            Multiline = true,
            WordWrap = true,
            TabKeyAddsTab = false,
        };
        _composer.ContentsChanged += (_, _) => OnComposerChanged();
        _composer.HasFocusChanged += (_, e) =>
        {
            // a click into the composer starts insert mode
            if (e.NewValue && Mode == InputMode.Normal)
            {
                SetMode(InputMode.Insert);
            }
        };
        _mainFrame.Add(_timelineView, _pageView, _rule, _composer);

        _status = new Label { Y = Pos.AnchorEnd(1), Width = Dim.Fill() };
        _commandPrefix = new Label
        {
            Y = Pos.AnchorEnd(1),
            Width = 1,
            Text = ":",
            Visible = false,
        };
        _commandLine = new TextField
        {
            X = 1,
            Y = Pos.AnchorEnd(1),
            Width = Dim.Fill(),
            Visible = false,
        };
        _commandLine.TextChanged += (_, _) => OnCommandLineChanged();

        Add(_roomsFrame, _mainFrame, _status, _commandPrefix, _commandLine);
        _roomsView.SetFocus();
        ShowRooms();
        UpdateStatus();

        IsRunningChanged += (sender, e) =>
        {
            if (e.Value)
            {
                _app.Keyboard.KeyDown += OnKeyDown;
                _ = StartSessionAsync();
            }
            else
            {
                _app.Keyboard.KeyDown -= OnKeyDown;
            }
        };
        ViewportChanged += (_, _) => UpdateStatus();
    }

    /// <summary>
    /// Whether the user closed the window with <c>:logout</c> to log out.
    /// </summary>
    public bool IsLogoutRequested { get; private set; }

    /// <summary>
    /// The room that is open, null if there is none.
    /// </summary>
    public string? OpenRoomId => _room?.RoomId;

    private async Task StartSessionAsync()
    {
        _session.RoomsChanged += (_, _) => Post(ShowRooms);
        _session.SyncStateChanged += (_, state) =>
        {
            _syncState = state.ToString().ToLowerInvariant();
            Post(() => UpdateStatus());
        };
        _session.Encryption.Changed += (_, _) => Post(OnEncryptionChanged);
        _session.Verification.Changed += (_, _) => Post(OnVerificationChanged);
        _session.NotificationReceived += (_, notification) => Post(() => OnNotification(notification));
        try
        {
            await _session.StartAsync();
        }
        catch (Exception e) when (e is ClientException or InvalidOperationException)
        {
            ShowError($"Starting the sync failed: {e.Message}");
        }
    }

    /// <summary>
    /// Runs <paramref name="action"/> on the main loop, unless the window is closed by then.
    /// </summary>
    private void Post(System.Action action) =>
        _app.Invoke(() =>
        {
            if (!_disposed)
            {
                action();
            }
        });

    private void ShowRooms()
    {
        string? selectedId = _roomsView.SelectedItem is int i && i < _rooms.Count ? _rooms[i].RoomId : null;
        IReadOnlyList<RoomSummary> rooms = _session.Rooms;
        _rooms = rooms;
        int? selected = null;
        if (selectedId is not null)
        {
            int index = IndexOfRoom(selectedId);
            selected = index >= 0 ? index : null;
        }
        string? openId = _room?.RoomId;
        _roomsView.SetItems(rooms.Count, width => RoomListRenderer.Layout(rooms, width, openId), selected);
        int unread = rooms.Count(r => r.IsUnread);
        string title = unread > 0 ? $"Rooms ({unread} unread)" : "Rooms";
        _roomsFrame.Title = string.IsNullOrEmpty(_session.RoomFilter) ? title : $"Rooms /{_session.RoomFilter}";
    }

    private int IndexOfRoom(string roomId)
    {
        for (int i = 0; i < _rooms.Count; i++)
        {
            if (_rooms[i].RoomId == roomId)
            {
                return i;
            }
        }
        return -1;
    }

    private void OpenSelectedRoom()
    {
        if (_roomsView.SelectedItem is int index && index < _rooms.Count)
        {
            OpenRoom(_rooms[index].RoomId);
        }
    }

    private void OpenRoom(string roomId)
    {
        _ = OpenRoomAsync(roomId);
    }

    private async Task OpenRoomAsync(string roomId)
    {
        // updates of a previously opened room can still be queued, they are ignored
        int generation = ++_roomGeneration;
        OpenedRoom? previous = _room;
        _room = null;
        _entries = [];
        CancelReplyAndEdit();
        ClosePage();
        string name = _rooms.FirstOrDefault(r => r.RoomId == roomId)?.Name ?? roomId;
        _mainFrame.Title = name;
        _timelineView.Placeholder = "Loading…";
        ShowTimeline();
        ShowRooms();

        try
        {
            if (previous is not null)
            {
                await previous.DisposeAsync();
            }
            Room nativeRoom =
                _session.GetRoom(roomId)
                ?? throw new InvalidOperationException("The room is unknown, it may not have synced yet.");
            OpenedRoom opened = await OpenedRoom.OpenAsync(_session.Client, nativeRoom, _session.UserId);
            if (generation != _roomGeneration || _disposed)
            {
                // another room was opened in the meantime
                await opened.DisposeAsync();
                return;
            }
            _room = opened;
            opened.EntriesChanged += (_, _) => Post(() => OnRoomUpdate(generation, ShowTimeline));
            opened.TypingChanged += (_, _) => Post(() => OnRoomUpdate(generation, UpdateRule));
            opened.SummaryChanged += (_, _) => Post(() => OnRoomUpdate(generation, ShowRoomSummary));
            _timelineView.Placeholder = "No messages yet.";
            ShowRoomSummary();
            ShowTimeline();
            ShowRooms();
            if (opened.IsInvite)
            {
                ShowPage(Pages.Invite(opened.Summary));
                Ask(
                    $"Accept the invite to {opened.Summary.Name}? (y/n)",
                    accept =>
                    {
                        if (accept)
                        {
                            AcceptInvite();
                        }
                        else
                        {
                            DeclineInvite();
                        }
                    }
                );
            }
            else if (FocusedPane != Pane.Rooms || Mode == InputMode.Normal)
            {
                SetMode(InputMode.Insert);
            }
        }
        catch (Exception e) when (e is ClientException or InvalidOperationException or ObjectDisposedException)
        {
            if (generation == _roomGeneration)
            {
                _timelineView.Placeholder = "The room can't be opened.";
                ShowTimeline();
                ShowError($"Opening {name} failed: {e.Message}");
            }
        }
    }

    private void OnRoomUpdate(int generation, System.Action update)
    {
        if (generation == _roomGeneration && _room is not null)
        {
            update();
        }
    }

    private async Task CloseRoomAsync()
    {
        _roomGeneration++;
        OpenedRoom? room = _room;
        _room = null;
        _entries = [];
        CancelReplyAndEdit();
        ClosePage();
        _mainFrame.Title = "Timeline";
        _timelineView.Placeholder = "Select a room and press Enter to open it, ? shows the keys.";
        ShowTimeline();
        ShowRooms();
        UpdateRule();
        if (room is not null)
        {
            await room.DisposeAsync();
        }
    }

    private void ShowRoomSummary()
    {
        if (_room is not { Summary: var summary })
        {
            return;
        }
        string details = string.Join(
            " │ ",
            new[]
            {
                summary.IsEncrypted ? "encrypted" : null,
                summary.JoinedMembers > 0 ? $"{summary.JoinedMembers} members" : null,
                summary.Topic?.ReplaceLineEndings(" "),
            }.OfType<string>()
        );
        _mainFrame.Title = details.Length > 0 ? $"{summary.Name} │ {details}" : summary.Name;
        if (_page?.Kind == PageKind.Invite && !summary.IsInvite)
        {
            ClosePage();
        }
    }

    private void ShowTimeline()
    {
        string? selectedKey = _timelineView.SelectedItem is int i && i < _entries.Count ? _entries[i].Key : null;
        bool following = IsFollowingTimeline;
        IReadOnlyList<TimelineEntry> entries = _room?.Entries ?? [];
        _entries = entries;

        int? selected = null;
        if (!following && selectedKey is not null)
        {
            for (int index = 0; index < entries.Count; index++)
            {
                if (entries[index].Key == selectedKey)
                {
                    selected = index;
                    break;
                }
            }
        }
        DateTimeOffset now = _settings.Clock.GetLocalNow();
        _timelineView.SetItems(entries.Count, width => TimelineRenderer.Layout(entries, width, now), selected);
        MarkAsReadIfFollowing();
    }

    /// <summary>
    /// Sends a read receipt when the user sees the newest message: the timeline follows the end.
    /// </summary>
    private void MarkAsReadIfFollowing()
    {
        if (_room is not { IsInvite: false } room || _entries.Count == 0)
        {
            return;
        }
        bool following = IsFollowingTimeline;
        string lastKey = _entries[^1].Key;
        if (following && lastKey != _lastReadKey)
        {
            _lastReadKey = lastKey;
            _ = MarkAsReadAsync(room);
        }
    }

    private static async Task MarkAsReadAsync(OpenedRoom room)
    {
        try
        {
            await room.MarkAsReadAsync();
        }
        catch (Exception e) when (e is ClientException or ObjectDisposedException or InvalidOperationException)
        {
            // the next message tries again
        }
    }

    /// <summary>
    /// Whether the timeline shows the newest message and follows new ones.
    /// </summary>
    private bool IsFollowingTimeline =>
        _timelineView.SelectedItem is not int selected || selected >= _entries.Count - 1;

    private void OnTimelineSelectionChanged()
    {
        // reaching the top loads older messages
        if (_room is { } room && _timelineView.SelectedItem is int selected && selected <= 2 && !room.ReachedStart)
        {
            Run(null, room.PaginateBackwardsAsync);
        }
        MarkAsReadIfFollowing();
    }

    private void UpdateRule()
    {
        List<string> parts = [];
        if (_editing is { } editing)
        {
            parts.Add($"editing: {Preview(editing)} (Esc in normal mode cancels)");
        }
        else if (_replyTo is { } reply)
        {
            parts.Add($"replying to {reply.SenderName}: {Preview(reply)} (Esc in normal mode cancels)");
        }
        IReadOnlyList<string> typing = _room?.TypingUsers ?? [];
        if (typing.Count > 0)
        {
            parts.Add(
                typing.Count switch
                {
                    1 => $"{typing[0]} is typing…",
                    2 => $"{typing[0]} and {typing[1]} are typing…",
                    _ => $"{typing.Count} people are typing…",
                }
            );
        }
        _rule.Label = string.Join(" │ ", parts);
    }

    private static string Preview(TimelineEntry entry) =>
        TextLayout.Truncate((entry.Media?.Filename ?? entry.Body).ReplaceLineEndings(" "), 40);

    private void OnComposerChanged()
    {
        int lines = Math.Clamp(_composer.Lines, 1, MaxComposerLines);
        if (_composerLines != lines)
        {
            _composerLines = lines;
            _composer.Height = lines;
            _composer.Y = Pos.AnchorEnd(lines);
            _rule.Y = Pos.AnchorEnd(lines + 1);
            _timelineView.Height = Dim.Fill(lines + 1);
            _pageView.Height = Dim.Fill(lines + 1);
        }
        if (_room is { IsInvite: false } room && Mode == InputMode.Insert)
        {
            _ = SetTypingAsync(room, _composer.Text.Length > 0);
        }
    }

    private static async Task SetTypingAsync(OpenedRoom room, bool isTyping)
    {
        try
        {
            await room.SetTypingAsync(isTyping);
        }
        catch (Exception e) when (e is ClientException or ObjectDisposedException)
        {
            // typing notices are best effort
        }
    }

    private void ShowPage(Page page)
    {
        _page = page;
        _mainFrame.Title = page.Title;
        _pageView.SetItems(page.Lines.Count, page.Layout, 0);
        _pageView.Visible = true;
        _timelineView.Visible = false;
        if (Mode == InputMode.Normal)
        {
            FocusPane(Pane.Page);
        }
    }

    /// <summary>
    /// Replaces the page if a page of the same kind is shown, for pages whose data changed.
    /// </summary>
    private void RefreshPage(Page page)
    {
        if (_page?.Kind == page.Kind)
        {
            int? selected = _pageView.SelectedItem;
            _page = page;
            _mainFrame.Title = page.Title;
            _pageView.SetItems(page.Lines.Count, page.Layout, selected);
        }
    }

    private void ClosePage()
    {
        if (_page is null)
        {
            return;
        }
        _page = null;
        _pageView.Visible = false;
        _timelineView.Visible = true;
        if (_room is not null)
        {
            ShowRoomSummary();
        }
        else
        {
            _mainFrame.Title = "Timeline";
        }
        if (FocusedPane == Pane.Page)
        {
            FocusPane(Pane.Timeline);
        }
    }

    private void OnEncryptionChanged()
    {
        UpdateStatus();
        RefreshPage(Pages.Encryption(_session.Encryption, _session.UserId, _session.DeviceId, _shownRecoveryKey));
    }

    private void OnVerificationChanged()
    {
        VerificationStatus status = _session.Verification.Status;
        Page page = Pages.Verification(status);
        switch (status.Step)
        {
            case VerificationStep.Incoming:
                ShowPage(page);
                Ask(
                    $"{status.Partner} wants to verify. Accept? (y/n)",
                    accept =>
                    {
                        if (accept)
                        {
                            Run("Accepting the verification…", _session.Verification.AcceptAsync);
                        }
                        else
                        {
                            Run(null, _session.Verification.CancelAsync);
                        }
                    }
                );
                break;
            case VerificationStep.Comparing:
                ShowPage(page);
                Ask(
                    "Do the emojis match? (y/n)",
                    match =>
                    {
                        if (match)
                        {
                            Run("Confirming…", _session.Verification.ConfirmAsync);
                        }
                        else
                        {
                            Run("Cancelling the verification…", _session.Verification.MismatchAsync);
                        }
                    }
                );
                break;
            case VerificationStep.Done:
                ClearPrompt();
                RefreshPage(page);
                ShowMessage($"Verified with {status.Partner}.");
                break;
            case VerificationStep.Cancelled:
            case VerificationStep.Failed:
                ClearPrompt();
                RefreshPage(page);
                ShowError(
                    status.Step == VerificationStep.Failed
                        ? "The verification failed."
                        : "The verification was cancelled."
                );
                break;
            default:
                RefreshPage(page);
                break;
        }
    }

    private void OnNotification(IncomingNotification notification)
    {
        // the open room counts as read while the timeline follows new messages
        bool reading = _room?.RoomId == notification.RoomId && IsFollowingTimeline;
        if (
            _notificationsEnabled
            && NotificationPolicy.ShouldNotify(notification, reading ? notification.RoomId : null, _startedAt)
        )
        {
            (string title, string body) = NotificationPolicy.Format(notification);
            _settings.Notifier.Notify(title, body);
        }
    }

    /// <summary>
    /// Closes the opened room and disposes the window.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        _app.Keyboard.KeyDown -= OnKeyDown;
        if (_room is not null)
        {
            await _room.DisposeAsync();
            _room = null;
        }
        Dispose();
    }
}

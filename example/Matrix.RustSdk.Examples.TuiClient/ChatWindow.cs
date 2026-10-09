using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Matrix.RustSdk.Examples.TuiClient;

/// <summary>
/// The main window: the room list on the left, the timeline of the opened room and the composer on the right.
/// </summary>
/// <remarks>
/// Views may only be changed on the main loop of Terminal.Gui. The session reports changes on other threads, so every
/// update is moved to the main loop with <see cref="IApplication.Invoke(System.Action)"/>, which renders the latest
/// state.
/// </remarks>
public sealed class ChatWindow : Window, IAsyncDisposable
{
    private readonly IApplication _app;
    private readonly MatrixSession _session;
    private readonly ListView _roomList;
    private readonly FrameView _timelineFrame;
    private readonly ListView _timelineView;
    private readonly TextField _composer;
    private readonly Label _status;

    private IReadOnlyList<RoomSummary> _rooms = [];
    private RoomTimeline? _timeline;
    private int _timelineGeneration;
    private string _syncState = "starting";

    public ChatWindow(IApplication app, MatrixSession session)
    {
        _app = app;
        _session = session;
        Title = $"Matrix TUI client - {session.UserId}";

        FrameView roomsFrame = new()
        {
            Title = "Rooms",
            Width = Dim.Percent(30),
            Height = Dim.Fill(1),
        };
        _roomList = new ListView { Width = Dim.Fill(), Height = Dim.Fill() };
        _roomList.SetSource(new ObservableCollection<string>());
        // Enter (or a double click) opens the selected room
        _roomList.Accepting += (_, e) =>
        {
            e.Handled = true;
            OpenSelectedRoom();
        };
        roomsFrame.Add(_roomList);

        _timelineFrame = new FrameView
        {
            Title = "Timeline",
            X = Pos.Right(roomsFrame),
            Width = Dim.Fill(),
            Height = Dim.Fill(4),
        };
        _timelineView = new ListView { Width = Dim.Fill(), Height = Dim.Fill() };
        _timelineView.SetSource(new ObservableCollection<string>(["Select a room and press Enter to open it."]));
        _timelineFrame.Add(_timelineView);

        FrameView composerFrame = new()
        {
            Title = "Message (Enter to send)",
            X = Pos.Right(roomsFrame),
            Y = Pos.Bottom(_timelineFrame),
            Width = Dim.Fill(),
            Height = 3,
        };
        _composer = new TextField { Width = Dim.Fill() };
        _composer.Accepting += (_, e) =>
        {
            e.Handled = true;
            SendComposerText();
        };
        composerFrame.Add(_composer);

        _status = new Label { Y = Pos.AnchorEnd(1), Width = Dim.Fill() };
        UpdateStatus();

        Add(roomsFrame, _timelineFrame, composerFrame, _status);
        _roomList.SetFocus();

        KeyDown += (_, key) =>
        {
            if (key == Key.L.WithCtrl)
            {
                key.Handled = true;
                IsLogoutRequested = true;
                RequestStop();
            }
        };

        IsRunningChanged += (sender, e) =>
        {
            if (e.Value)
            {
                _ = StartSyncAsync();
            }
        };
    }

    /// <summary>
    /// Whether the user closed the window with Ctrl+L to log out.
    /// </summary>
    public bool IsLogoutRequested { get; private set; }

    private async Task StartSyncAsync()
    {
        try
        {
            await _session.StartAsync(
                () => _app.Invoke(ShowRooms),
                state =>
                    _app.Invoke(() =>
                    {
                        _syncState = state.ToString().ToLowerInvariant();
                        UpdateStatus();
                    })
            );
        }
        catch (Exception e)
        {
            UpdateStatus($"Starting the sync failed: {e.Message}");
        }
    }

    private void ShowRooms()
    {
        IReadOnlyList<RoomSummary> rooms = _session.Rooms;
        int? selected = _roomList.SelectedItem;
        _rooms = rooms;
        _roomList.SetSource(new ObservableCollection<string>(rooms.Select(r => r.ToString())));
        if (rooms.Count > 0)
        {
            _roomList.SelectedItem = Math.Min(selected ?? 0, rooms.Count - 1);
        }
    }

    private void OpenSelectedRoom()
    {
        if (_roomList.SelectedItem is int index && index < _rooms.Count)
        {
            _ = OpenRoomAsync(_rooms[index]);
        }
    }

    private async Task OpenRoomAsync(RoomSummary room)
    {
        // updates of a previously opened timeline can still be queued, they are ignored
        int generation = ++_timelineGeneration;
        RoomTimeline? previous = _timeline;
        _timeline = null;
        _timelineFrame.Title = room.Name;
        ShowTimeline([room.IsInvite ? "Joining..." : "Loading..."]);

        try
        {
            if (previous is not null)
            {
                await previous.DisposeAsync();
            }
            // the room list only keeps the id, the timeline owns the room
            Bindings.Room nativeRoom =
                _session.GetRoom(room.RoomId) ?? throw new InvalidOperationException("The room is gone.");
            RoomTimeline? opened = null;
            opened = await RoomTimeline.OpenAsync(
                nativeRoom,
                () =>
                    _app.Invoke(() =>
                    {
                        if (generation == _timelineGeneration && opened is not null)
                        {
                            ShowTimeline(opened.Lines);
                        }
                    })
            );
            if (generation != _timelineGeneration)
            {
                // another room was opened in the meantime
                await opened.DisposeAsync();
                return;
            }
            ShowTimeline(opened.Lines);
            _timeline = opened;
            _composer.SetFocus();
            UpdateStatus();
        }
        catch (Exception e)
        {
            UpdateStatus($"Opening {room.Name} failed: {e.Message}");
        }
    }

    private void ShowTimeline(IReadOnlyList<string> lines)
    {
        _timelineView.SetSource(new ObservableCollection<string>(lines));
        // keep the newest message in view
        _timelineView.MoveEnd();
    }

    private void SendComposerText()
    {
        string text = _composer.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }
        if (_timeline is null)
        {
            UpdateStatus("Open a room before sending a message.");
            return;
        }
        _composer.Text = "";
        _ = SendAsync(_timeline, text);
    }

    private async Task SendAsync(RoomTimeline timeline, string text)
    {
        try
        {
            await timeline.SendAsync(text);
        }
        catch (Exception e)
        {
            UpdateStatus($"Sending failed: {e.Message}");
        }
    }

    private void UpdateStatus(string? message = null) =>
        _status.Text =
            message ?? $"Sync: {_syncState} | Enter: open room / send | Tab: next pane | Ctrl+L: log out | Esc: quit";

    /// <summary>
    /// Closes the opened timeline and disposes the window.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_timeline is not null)
        {
            await _timeline.DisposeAsync();
            _timeline = null;
        }
        Dispose();
    }
}

using System.Collections.ObjectModel;
using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Matrix.RustSdk.Examples.TuiClient;

/// <summary>
/// The main window: the room list on the left, the timeline of the opened room and the composer on the right.
/// </summary>
/// <remarks>
/// Views may only be changed on the main loop of Terminal.Gui. The SDK calls the listeners on its own threads, so every
/// update is moved to the main loop with <see cref="IApplication.Invoke(System.Action)"/>.
/// </remarks>
public sealed class ChatWindow : Window
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

        IsRunningChanged += (_, e) =>
        {
            if (e.Value)
            {
                _ = StartSyncAsync();
            }
        };
    }

    private async Task StartSyncAsync()
    {
        try
        {
            await _session.StartAsync(
                rooms => _app.Invoke(() => ShowRooms(rooms)),
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

    private void ShowRooms(IReadOnlyList<RoomSummary> rooms)
    {
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
        _timeline?.Dispose();
        _timeline = null;
        _timelineFrame.Title = room.Name;
        ShowTimeline([room.IsInvite ? "Joining..." : "Loading..."]);

        try
        {
            RoomTimeline timeline = await RoomTimeline.OpenAsync(
                room.Room,
                lines =>
                    _app.Invoke(() =>
                    {
                        if (generation == _timelineGeneration)
                        {
                            ShowTimeline(lines);
                        }
                    })
            );
            if (generation != _timelineGeneration)
            {
                // another room was opened in the meantime
                timeline.Dispose();
                return;
            }
            _timeline = timeline;
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
        _status.Text = message ?? $"Sync: {_syncState} | Enter: open room / send | Tab: next pane | Esc: quit";

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timeline?.Dispose();
            _timeline = null;
        }
        base.Dispose(disposing);
    }
}

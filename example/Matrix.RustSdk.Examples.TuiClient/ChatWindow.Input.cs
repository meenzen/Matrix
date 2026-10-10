using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Examples.TuiClient.Chat;
using Matrix.RustSdk.Examples.TuiClient.Input;
using Matrix.RustSdk.Examples.TuiClient.Rendering;
using Terminal.Gui.Input;

namespace Matrix.RustSdk.Examples.TuiClient;

public sealed partial class ChatWindow
{
    private enum Pane
    {
        Rooms,
        Timeline,
        Page,
    }

    private readonly VimKeymap _keymap = new();
    private readonly List<string> _history = [];
    private int _historyIndex;
    private Pane _pane = Pane.Rooms;
    private TimelineEntry? _replyTo;
    private TimelineEntry? _editing;
    private string? _message;
    private bool _messageIsError;
    private (string Question, Action<bool> Answer)? _prompt;

    private InputMode Mode { get; set; } = InputMode.Normal;

    private Pane FocusedPane => _pane;

    /// <summary>
    /// The pane on the right: the page if one is shown, otherwise the timeline.
    /// </summary>
    private Pane MainPane => _page is null ? Pane.Timeline : Pane.Page;

    private RowsView FocusedList =>
        _pane switch
        {
            Pane.Rooms => _roomsView,
            Pane.Page => _pageView,
            _ => _timelineView,
        };

    /// <summary>
    /// Handles every key before the views see it: in normal mode all keys are commands, in the other modes the keys
    /// the keymap doesn't handle go to the focused text field.
    /// </summary>
    private void OnKeyDown(object? sender, Key key)
    {
        if (_disposed || _app.TopRunnableView != this)
        {
            return;
        }

        if (_prompt is { } prompt && Mode == InputMode.Normal)
        {
            key.Handled = true;
            if (key == new Key('y') || key == new Key('Y'))
            {
                ClearPrompt();
                prompt.Answer(true);
            }
            else if (key == new Key('n') || key == new Key('N') || key == Key.Esc)
            {
                ClearPrompt();
                prompt.Answer(false);
            }
            return;
        }

        KeyResult result = _keymap.Map(Mode, key);
        switch (result.Kind)
        {
            case KeyResultKind.PassThrough:
                return;
            case KeyResultKind.Action:
                key.Handled = true;
                Dispatch(result.Action);
                break;
            default:
                key.Handled = true;
                break;
        }
        UpdateStatus();
    }

    private void Dispatch(KeyAction keyAction)
    {
        int count = keyAction.Count;
        switch (keyAction.Action)
        {
            case InputAction.MoveDown:
                FocusedList.MoveBy(count);
                break;
            case InputAction.MoveUp:
                FocusedList.MoveBy(-count);
                break;
            case InputAction.MoveFirst:
                FocusedList.MoveFirst();
                break;
            case InputAction.MoveLast:
                FocusedList.MoveLast();
                break;
            case InputAction.HalfPageDown:
                FocusedList.MoveByRows(count * Math.Max(1, FocusedList.PageItems / 2));
                break;
            case InputAction.HalfPageUp:
                FocusedList.MoveByRows(-count * Math.Max(1, FocusedList.PageItems / 2));
                break;
            case InputAction.PageDown:
                FocusedList.MoveByRows(count * FocusedList.PageItems);
                break;
            case InputAction.PageUp:
                FocusedList.MoveByRows(-count * FocusedList.PageItems);
                break;
            case InputAction.FocusRooms:
                FocusPane(Pane.Rooms);
                break;
            case InputAction.FocusTimeline:
                FocusPane(MainPane);
                break;
            case InputAction.ToggleFocus:
                FocusPane(_pane == Pane.Rooms ? MainPane : Pane.Rooms);
                break;
            case InputAction.Open:
                Open();
                break;
            case InputAction.NextRoom:
                _roomsView.MoveBy(count);
                OpenSelectedRoom(startInsert: false);
                break;
            case InputAction.PreviousRoom:
                _roomsView.MoveBy(-count);
                OpenSelectedRoom(startInsert: false);
                break;
            case InputAction.NextUnreadRoom:
                OpenNextUnreadRoom();
                break;
            case InputAction.Insert:
            case InputAction.Append:
                StartInsert();
                break;
            case InputAction.Normal:
                SetMode(InputMode.Normal);
                break;
            case InputAction.Send:
                SendComposerText();
                break;
            case InputAction.NewLine:
                _composer.InsertText("\n");
                break;
            case InputAction.Command:
                StartCommandLine(InputMode.Command, "");
                break;
            case InputAction.Filter:
                StartCommandLine(InputMode.Filter, _session.RoomFilter);
                break;
            case InputAction.Execute:
                ExecuteCommandLine();
                break;
            case InputAction.HistoryPrevious:
                BrowseHistory(-1);
                break;
            case InputAction.HistoryNext:
                BrowseHistory(1);
                break;
            case InputAction.Complete:
                Complete();
                break;
            case InputAction.Reply:
                StartReply();
                break;
            case InputAction.Edit:
                StartEdit();
                break;
            case InputAction.Delete:
                DeleteSelected();
                break;
            case InputAction.React:
                if (SelectedEntry("react to") is not null)
                {
                    StartCommandLine(InputMode.Command, "react ");
                }
                break;
            case InputAction.Yank:
                YankSelected();
                break;
            case InputAction.OpenMedia:
                OpenSelectedMedia();
                break;
            case InputAction.Members:
                ShowMembers();
                break;
            case InputAction.Help:
                ShowPage(Pages.Help());
                break;
            case InputAction.Quit:
                RequestStop();
                break;
            case InputAction.Cancel:
                Cancel();
                break;
        }
    }

    private void SetMode(InputMode mode)
    {
        if (Mode == InputMode.Insert && mode != InputMode.Insert && _room is { IsInvite: false } room)
        {
            _ = SetTypingAsync(room, false);
        }
        Mode = mode;
        _keymap.Reset();
        _composer.CanFocus = mode == InputMode.Insert;
        bool line = mode is InputMode.Command or InputMode.Filter;
        _commandLine.Visible = line;
        _commandPrefix.Visible = line;
        _status.Visible = !line;
        switch (mode)
        {
            case InputMode.Insert:
                // leaving insert mode goes back to the timeline
                _pane = Pane.Timeline;
                _composer.SetFocus();
                break;
            case InputMode.Command:
            case InputMode.Filter:
                _commandPrefix.Text = mode == InputMode.Command ? ":" : "/";
                _commandLine.SetFocus();
                break;
            default:
                FocusPane(_pane == Pane.Page && _page is null ? Pane.Timeline : _pane);
                break;
        }
        UpdateStatus();
    }

    private void FocusPane(Pane pane)
    {
        _pane = pane;
        if (Mode == InputMode.Insert)
        {
            Mode = InputMode.Normal;
        }
        FocusedList.SetFocus();
        UpdateStatus();
    }

    private void Open()
    {
        switch (_pane)
        {
            case Pane.Rooms:
                OpenSelectedRoom();
                break;
            case Pane.Page when _page?.Open is { } open && _pageView.SelectedItem is int item:
                Run(null, () => open(item));
                break;
            case Pane.Timeline:
                OpenSelectedMedia();
                break;
        }
    }

    private void OpenNextUnreadRoom()
    {
        int start = _roomsView.SelectedItem ?? -1;
        for (int offset = 1; offset <= _rooms.Count; offset++)
        {
            int index = (start + offset) % _rooms.Count;
            if (_rooms[index].IsUnread && _rooms[index].RoomId != _room?.RoomId)
            {
                _roomsView.Select(index);
                OpenRoom(_rooms[index].RoomId, startInsert: false);
                return;
            }
        }
        ShowMessage("There are no unread rooms.");
    }

    private void StartInsert()
    {
        if (_room is null)
        {
            ShowError("Open a room before writing a message.");
            return;
        }
        if (_room.IsInvite)
        {
            ShowError("Accept the invite first (:accept).");
            return;
        }
        ClosePage();
        SetMode(InputMode.Insert);
    }

    private void Cancel()
    {
        switch (Mode)
        {
            case InputMode.Command:
                SetMode(InputMode.Normal);
                break;
            case InputMode.Filter:
                _session.RoomFilter = "";
                SetMode(InputMode.Normal);
                FocusPane(Pane.Rooms);
                break;
            default:
                if (_page is not null)
                {
                    if (_page.Kind == PageKind.Verification && _session.Verification.Status.IsActive)
                    {
                        Run("Cancelling the verification…", _session.Verification.CancelAsync);
                    }
                    else if (_page.Kind == PageKind.Verification)
                    {
                        _session.Verification.Dismiss();
                    }
                    ClosePage();
                }
                else if (_replyTo is not null || _editing is not null)
                {
                    if (_editing is not null)
                    {
                        _composer.Text = "";
                    }
                    CancelReplyAndEdit();
                }
                else
                {
                    _message = null;
                }
                break;
        }
    }

    private void CancelReplyAndEdit()
    {
        _replyTo = null;
        _editing = null;
        UpdateRule();
    }

    private void SendComposerText()
    {
        string text = _composer.Text.Trim();
        if (text.Length == 0)
        {
            return;
        }
        if (_room is not { } room)
        {
            ShowError("Open a room before sending a message.");
            return;
        }
        TimelineEntry? replyTo = _replyTo;
        TimelineEntry? editing = _editing;
        _composer.Text = "";
        CancelReplyAndEdit();
        // following the timeline again shows the message
        _timelineView.MoveLast();
        if (editing is not null)
        {
            Run(null, () => room.EditAsync(editing, text), failure: "Editing failed");
        }
        else
        {
            Run(null, () => room.SendAsync(text, replyTo), failure: "Sending failed");
        }
    }

    /// <summary>
    /// The message selected in the timeline, null with an error message if there is none.
    /// </summary>
    private TimelineEntry? SelectedEntry(string action)
    {
        if (_pane != Pane.Timeline || _timelineView.SelectedItem is not int index || index >= _entries.Count)
        {
            ShowError($"Select the message to {action} in the timeline first (l, then j/k).");
            return null;
        }
        TimelineEntry entry = _entries[index];
        if (!entry.IsEvent)
        {
            ShowError($"Select a message to {action}.");
            return null;
        }
        return entry;
    }

    private void StartReply()
    {
        if (SelectedEntry("reply to") is not { } entry)
        {
            return;
        }
        if (!entry.CanReply)
        {
            ShowError("You can't reply to this message.");
            return;
        }
        _editing = null;
        _replyTo = entry;
        UpdateRule();
        SetMode(InputMode.Insert);
    }

    private void StartEdit()
    {
        if (SelectedEntry("edit") is not { } entry)
        {
            return;
        }
        if (!entry.IsEditable || entry.Kind is not (EntryKind.Message or EntryKind.Emote or EntryKind.Notice))
        {
            ShowError("Only your own text messages can be edited.");
            return;
        }
        _replyTo = null;
        _editing = entry;
        _composer.Text = entry.Body;
        _composer.MoveEnd();
        UpdateRule();
        SetMode(InputMode.Insert);
    }

    private void DeleteSelected()
    {
        if (SelectedEntry("delete") is not { } entry || _room is not { } room)
        {
            return;
        }
        if (entry.Kind is EntryKind.Redacted or EntryKind.Event)
        {
            ShowError("This can't be deleted.");
            return;
        }
        Ask(
            $"Delete \"{Preview(entry)}\"? (y/n)",
            delete =>
            {
                if (delete)
                {
                    Run("Deleting…", () => room.RedactAsync(entry), "Deleted.");
                }
            }
        );
    }

    private void YankSelected()
    {
        if (SelectedEntry("copy") is not { } entry)
        {
            return;
        }
        string text = entry.Media?.Filename ?? entry.Body;
        if (_app.Clipboard is { IsSupported: true } clipboard && clipboard.TrySetClipboardData(text))
        {
            ShowMessage("Copied the message.");
        }
        else
        {
            ShowError("There is no clipboard to copy to.");
        }
    }

    private void StartCommandLine(InputMode mode, string text)
    {
        _historyIndex = _history.Count;
        SetMode(mode);
        _commandLine.Text = text;
        _commandLine.InsertionPoint = text.Length;
    }

    private void OnCommandLineChanged()
    {
        if (Mode == InputMode.Filter)
        {
            _session.RoomFilter = _commandLine.Text;
            _roomsView.MoveFirst();
        }
    }

    private void ExecuteCommandLine()
    {
        string text = _commandLine.Text;
        if (Mode == InputMode.Filter)
        {
            SetMode(InputMode.Normal);
            FocusPane(Pane.Rooms);
            if (_rooms.Count > 0)
            {
                _roomsView.MoveFirst();
            }
            return;
        }
        SetMode(InputMode.Normal);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }
        if (_history.Count == 0 || _history[^1] != text)
        {
            _history.Add(text);
        }
        ParsedCommand parsed = CommandParser.Parse(text);
        if (parsed.Command is { } command)
        {
            Execute(command);
        }
        else
        {
            ShowError(parsed.Error ?? "Invalid command.");
        }
    }

    private void BrowseHistory(int direction)
    {
        if (Mode != InputMode.Command || _history.Count == 0)
        {
            return;
        }
        _historyIndex = Math.Clamp(_historyIndex + direction, 0, _history.Count);
        string text = _historyIndex < _history.Count ? _history[_historyIndex] : "";
        _commandLine.Text = text;
        _commandLine.InsertionPoint = text.Length;
    }

    private void Complete()
    {
        if (Mode == InputMode.Command)
        {
            if (CommandParser.Complete(_commandLine.Text) is { } completed)
            {
                _commandLine.Text = completed;
                _commandLine.InsertionPoint = completed.Length;
            }
            return;
        }
        if (Mode == InputMode.Insert)
        {
            CompleteName();
        }
    }

    /// <summary>
    /// Completes the name of a sender at the end of the composer, like IRC clients do.
    /// </summary>
    private void CompleteName()
    {
        string text = _composer.Text;
        int start = text.LastIndexOfAny([' ', '\n']) + 1;
        string prefix = text[start..].TrimStart('@');
        if (prefix.Length == 0)
        {
            return;
        }
        string? name = _entries
            .Where(e => e.SenderName is not null && !e.IsOwn)
            .Reverse()
            .Select(e => e.SenderName!)
            .FirstOrDefault(n => n.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase));
        if (name is null)
        {
            return;
        }
        _composer.Text = text[..start] + name + (start == 0 ? ": " : " ");
        _composer.MoveEnd();
    }

    /// <summary>
    /// Asks a yes/no question in the status line, the next y or n (or Esc) in normal mode answers it.
    /// </summary>
    private void Ask(string question, Action<bool> answer)
    {
        if (Mode != InputMode.Normal)
        {
            SetMode(InputMode.Normal);
        }
        _prompt = (question, answer);
        UpdateStatus();
    }

    private void ClearPrompt()
    {
        _prompt = null;
        UpdateStatus();
    }

    /// <summary>
    /// Runs an action of the user: shows <paramref name="progress"/> while it runs, then <paramref name="done"/> or
    /// the error. Started on the main loop, the continuation runs on it again.
    /// </summary>
    private void Run(string? progress, Func<Task> action, string? done = null, string? failure = null)
    {
        if (progress is not null)
        {
            ShowMessage(progress);
        }
        _ = RunAsync();

        async Task RunAsync()
        {
            try
            {
                await action();
                if (done is not null)
                {
                    ShowMessage(done);
                }
                else if (progress is not null && _message == progress)
                {
                    ShowMessage(null);
                }
            }
            catch (Exception e)
                when (e
                        is ClientException
                            or InvalidOperationException
                            or IOException
                            or UnauthorizedAccessException
                            or ObjectDisposedException
                            or ArgumentException
                            or RecoveryException
                            or RoomException
                            or SteadyStateException
                            or ClientBuildException
                )
            {
                ShowError($"{failure ?? "Failed"}: {e.Message}");
            }
        }
    }

    private void ShowMessage(string? message)
    {
        if (_disposed)
        {
            return;
        }
        _message = message;
        _messageIsError = false;
        UpdateStatus();
    }

    private void ShowError(string message)
    {
        if (_disposed)
        {
            return;
        }
        _message = message;
        _messageIsError = true;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        string mode = Mode switch
        {
            InputMode.Insert => "-- INSERT --",
            InputMode.Command => "-- COMMAND --",
            InputMode.Filter => "-- FILTER --",
            _ => "",
        };
        string message = _messageIsError && _message is not null ? $"E: {_message}" : _message ?? "";
        string left = _prompt?.Question ?? string.Join(" ", new[] { mode, message }.Where(p => p.Length > 0));
        string pending = _keymap.PendingKeys;
        string right = string.Join(
            " │ ",
            new[]
            {
                pending.Length > 0 ? pending : null,
                $"sync: {_syncState}",
                _session.Encryption.Summary,
                _notificationsEnabled ? null : "notifications off",
                "? help",
            }.Where(p => !string.IsNullOrEmpty(p))
        );
        int width = Math.Max(_status.Viewport.Width, Viewport.Width);
        // the state on the right always stays visible, long messages are cut, but a question gets the whole line
        int rightWidth = TextLayout.Width(right);
        if (_prompt is not null || width - rightWidth < 20)
        {
            _status.Text = TextLayout.Truncate(left, width);
            return;
        }
        left = TextLayout.Truncate(left, width - rightWidth - 1);
        _status.Text = left + new string(' ', width - TextLayout.Width(left) - rightWidth) + right;
    }
}

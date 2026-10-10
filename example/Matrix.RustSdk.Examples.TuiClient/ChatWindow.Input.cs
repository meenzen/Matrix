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
    private const string InviteTag = "invite";
    private const string VerificationTag = "verification";
    private const int MaxHistory = 500;

    // yes/no questions, answered one after the other with y or n in normal mode, Esc dismisses one
    private readonly List<Prompt> _prompts = [];

    // a page that waits for normal mode, shown when the user stops writing
    private Page? _pendingPage;
    private bool _historyLoaded;

    // counts the key presses, a background action only changes the mode if the user didn't press keys meanwhile
    private long _keyPresses;

    private sealed record Prompt(string Question, System.Action OnYes, System.Action? OnNo, string? Tag);

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
        _keyPresses++;

        if (_prompts.Count > 0 && Mode == InputMode.Normal && AnswerPrompt(key))
        {
            key.Handled = true;
            UpdateStatus();
            return;
        }

        KeyResult result = _keymap.Map(Mode, key);
        switch (result.Kind)
        {
            case KeyResultKind.PassThrough:
                return;
            case KeyResultKind.Action:
                key.Handled = true;
                // like vim, the next command clears the message of the previous one
                if (Mode == InputMode.Normal)
                {
                    _message = null;
                }
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
                LoadOlderAtTheTop();
                break;
            case InputAction.MoveFirst:
                FocusedList.MoveFirst();
                LoadOlderAtTheTop();
                break;
            case InputAction.MoveLast when keyAction.HasCount:
                // 5G goes to the fifth item, like the fifth line in vim
                FocusedList.Select(count - 1);
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
                SelectOpenRoom();
                _roomsView.MoveBy(count);
                OpenSelectedRoom();
                break;
            case InputAction.PreviousRoom:
                SelectOpenRoom();
                _roomsView.MoveBy(-count);
                OpenSelectedRoom();
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
            case InputAction.Resend:
                ResendSelected();
                break;
            case InputAction.JumpToUnread:
                JumpToUnread();
                break;
            case InputAction.DeleteBack:
                DeleteBack();
                break;
            case InputAction.DeleteWord:
                DeleteBeforeCursor(wholeLine: false);
                break;
            case InputAction.DeleteLine:
                DeleteBeforeCursor(wholeLine: true);
                break;
        }
    }

    /// <summary>
    /// Moving to the top of the timeline loads older messages, also when the selection is there already.
    /// </summary>
    private void LoadOlderAtTheTop()
    {
        if (_pane == Pane.Timeline && _timelineView.SelectedItem is 0 && _room is { ReachedStart: false } room)
        {
            Run(null, room.PaginateBackwardsAsync);
        }
    }

    /// <summary>
    /// J and K move from the open room, not from wherever the selection of the room list is.
    /// </summary>
    private void SelectOpenRoom()
    {
        if (_room is { } room && IndexOfRoom(room.RoomId) is >= 0 and var index)
        {
            _roomsView.Select(index);
        }
    }

    private void JumpToUnread()
    {
        int marker = IndexOf(_entries, e => e.Kind == EntryKind.ReadMarker);
        if (marker < 0)
        {
            ShowMessage("There are no unread messages.");
            return;
        }
        FocusPane(Pane.Timeline);
        _timelineView.Select(marker);
    }

    private void ResendSelected()
    {
        if (SelectedEntry("send again") is not { } entry || _room is not { } room)
        {
            return;
        }
        if (entry.Status != SendStatus.Failed)
        {
            ShowError("Only messages that failed to send can be sent again.");
            return;
        }
        Run("Sending again…", () => room.ResendAsync(entry), failure: "Sending failed");
    }

    /// <summary>
    /// Backspace deletes the character before the cursor, on an empty command line it goes back to normal mode.
    /// </summary>
    private void DeleteBack()
    {
        string text = _commandLine.Text;
        if (text.Length == 0)
        {
            Cancel();
            return;
        }
        int cursor = Math.Clamp(_commandLine.InsertionPoint, 0, text.Length);
        if (cursor > 0)
        {
            _commandLine.Text = text.Remove(cursor - 1, 1);
            _commandLine.InsertionPoint = cursor - 1;
        }
    }

    /// <summary>
    /// C-w deletes the word before the cursor, C-u everything before it on the line, in the composer and the command
    /// line.
    /// </summary>
    private void DeleteBeforeCursor(bool wholeLine)
    {
        if (Mode == InputMode.Insert)
        {
            List<string> lines = [.. _composer.Text.ReplaceLineEndings("\n").Split('\n')];
            int row = Math.Clamp(_composer.CurrentRow, 0, lines.Count - 1);
            int column = Math.Clamp(_composer.CurrentColumn, 0, lines[row].Length);
            int start = wholeLine ? 0 : TextEditing.WordStart(lines[row], column);
            lines[row] = lines[row].Remove(start, column - start);
            _composer.Text = string.Join('\n', lines);
            _composer.InsertionPoint = new System.Drawing.Point(start, row);
            return;
        }
        string text = _commandLine.Text;
        int cursor = Math.Clamp(_commandLine.InsertionPoint, 0, text.Length);
        int from = wholeLine ? 0 : TextEditing.WordStart(text, cursor);
        _commandLine.Text = text.Remove(from, cursor - from);
        _commandLine.InsertionPoint = from;
    }

    /// <summary>
    /// Starts insert mode at the end of a background action that started at <paramref name="keyPresses"/>, unless the
    /// user pressed keys in the meantime: they would end up in the composer.
    /// </summary>
    private void StartInsertAfter(long keyPresses)
    {
        if (keyPresses == _keyPresses && Mode == InputMode.Normal)
        {
            SetMode(InputMode.Insert);
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
                if (_pendingPage is { } pending)
                {
                    _pendingPage = null;
                    ShowPage(pending);
                }
                break;
        }
        UpdateStatus();
    }

    /// <summary>
    /// A click into a list focuses it in normal mode, like moving there with h and l.
    /// </summary>
    private void OnClicked(Pane pane)
    {
        if (Mode != InputMode.Normal)
        {
            SetMode(InputMode.Normal);
        }
        FocusPane(pane);
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
                // Enter opens the room to read it, i starts writing
                OpenSelectedRoom(askInvite: true);
                FocusPane(Pane.Timeline);
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
                OpenRoom(_rooms[index].RoomId);
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
        Ask($"Delete \"{Preview(entry)}\"? (y/n)", () => Run("Deleting…", () => room.RedactAsync(entry), "Deleted."));
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
        LoadHistory();
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
            // like a quick switcher: the best match is opened and the filter cleared
            string? match = _rooms.Count > 0 ? _rooms[0].RoomId : null;
            _session.RoomFilter = "";
            SetMode(InputMode.Normal);
            if (match is null)
            {
                FocusPane(Pane.Rooms);
                ShowError($"No room matches \"{text}\".");
                return;
            }
            OpenRoom(match, askInvite: true);
            FocusPane(Pane.Timeline);
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
            SaveHistory();
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

    private string HistoryPath => Path.Join(_session.DataDirectory, "command-history");

    /// <summary>
    /// Loads the history of the command line, it is kept in the data directory across starts.
    /// </summary>
    private void LoadHistory()
    {
        if (_historyLoaded)
        {
            return;
        }
        _historyLoaded = true;
        try
        {
            if (File.Exists(HistoryPath))
            {
                _history.InsertRange(0, File.ReadAllLines(HistoryPath).Where(l => l.Length > 0).TakeLast(MaxHistory));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the history starts empty
        }
    }

    private void SaveHistory()
    {
        try
        {
            File.WriteAllLines(HistoryPath, _history.TakeLast(MaxHistory));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the history is only kept for this run
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
            IEnumerable<string> users = (_room?.KnownUsers.Keys ?? []).Where(u => u != _session.UserId);
            if (CommandParser.Complete(_commandLine.Text, users) is { } completed)
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
        // recent senders first, then the other members
        IEnumerable<string> names = _entries
            .Where(e => e.SenderName is not null && !e.IsOwn)
            .Reverse()
            .Select(e => e.SenderName!)
            .Concat(
                (_room?.KnownUsers ?? new Dictionary<string, string>())
                    .Where(u => u.Key != _session.UserId)
                    .Select(u => u.Value)
            );
        string? name = names.FirstOrDefault(n => n.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase));
        if (name is null)
        {
            return;
        }
        _composer.Text = text[..start] + name + (start == 0 ? ": " : " ");
        _composer.MoveEnd();
    }

    /// <summary>
    /// Asks a yes/no question in the status line. It is answered with y or n in normal mode, Esc dismisses it without
    /// doing anything. It never takes the user out of insert or command mode, questions wait one after the other.
    /// </summary>
    private void Ask(string question, System.Action onYes, System.Action? onNo = null, string? tag = null)
    {
        _prompts.Add(new Prompt(question, onYes, onNo, tag));
        UpdateStatus();
    }

    /// <summary>
    /// Removes the questions with <paramref name="tag"/>, for example when what they ask about is over.
    /// </summary>
    private void ClearPrompts(string tag)
    {
        _prompts.RemoveAll(p => p.Tag == tag);
        UpdateStatus();
    }

    private bool AnswerPrompt(Key key)
    {
        Prompt prompt = _prompts[0];
        if (key == new Key('y') || key == new Key('Y'))
        {
            _prompts.RemoveAt(0);
            prompt.OnYes();
            return true;
        }
        if (key == new Key('n') || key == new Key('N'))
        {
            _prompts.RemoveAt(0);
            prompt.OnNo?.Invoke();
            return true;
        }
        if (key == Key.Esc)
        {
            _prompts.RemoveAt(0);
            return true;
        }
        return false;
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
        string? question = _prompts.Count > 0 ? _prompts[0].Question : null;
        if (question is not null && Mode != InputMode.Normal)
        {
            question = $"{mode} (a question waits for normal mode: {question})";
        }
        string left = question ?? string.Join(" ", new[] { mode, message }.Where(p => p.Length > 0));
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
        if (_prompts.Count > 0 || width - rightWidth < 20)
        {
            _status.Text = TextLayout.Truncate(left, width);
            return;
        }
        left = TextLayout.Truncate(left, width - rightWidth - 1);
        _status.Text = left + new string(' ', width - TextLayout.Width(left) - rightWidth) + right;
    }
}

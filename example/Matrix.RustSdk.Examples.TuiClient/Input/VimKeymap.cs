using System.Text;
using Terminal.Gui.Input;

namespace Matrix.RustSdk.Examples.TuiClient.Input;

/// <summary>
/// The input modes, like vim: keys navigate in <see cref="Normal"/> mode, <see cref="Insert"/> mode types into the
/// composer, <see cref="Command"/> mode edits a <c>:</c> command and <see cref="Filter"/> mode the <c>/</c> filter of
/// the room list.
/// </summary>
public enum InputMode
{
    Normal,
    Insert,
    Command,
    Filter,
}

/// <summary>
/// What a key (or a sequence of keys like <c>gg</c>) does. The keymap only maps keys, the window decides what an
/// action means in the focused pane and which mode follows.
/// </summary>
public enum InputAction
{
    MoveDown,
    MoveUp,
    MoveFirst,
    MoveLast,
    HalfPageDown,
    HalfPageUp,
    PageDown,
    PageUp,
    FocusRooms,
    FocusTimeline,
    ToggleFocus,
    Open,
    NextRoom,
    PreviousRoom,
    NextUnreadRoom,
    Insert,
    Append,
    Normal,
    Send,
    NewLine,
    Command,
    Filter,
    Execute,
    HistoryPrevious,
    HistoryNext,
    Complete,
    Reply,
    Edit,
    Delete,
    React,
    Yank,
    OpenMedia,
    Members,
    Help,
    Quit,
    Cancel,
}

/// <summary>
/// An <see cref="InputAction"/> with the count typed before it (<c>5j</c>), 1 if there was none.
/// </summary>
public readonly record struct KeyAction(InputAction Action, int Count = 1);

/// <summary>
/// How the keymap handled a key.
/// </summary>
public enum KeyResultKind
{
    /// <summary>The key (sequence) maps to <see cref="KeyResult.Action"/>.</summary>
    Action,

    /// <summary>The key is part of an unfinished sequence like the <c>g</c> of <c>gg</c>.</summary>
    Pending,

    /// <summary>The key doesn't do anything, normal mode swallows unknown keys instead of typing them.</summary>
    Ignored,

    /// <summary>The key goes to the focused view, for example typing in insert mode.</summary>
    PassThrough,
}

/// <summary>
/// The result of a key press, <see cref="Action"/> is only set for <see cref="KeyResultKind.Action"/>.
/// </summary>
public readonly record struct KeyResult(KeyResultKind Kind, KeyAction Action = default)
{
    public static readonly KeyResult Pending = new(KeyResultKind.Pending);
    public static readonly KeyResult Ignored = new(KeyResultKind.Ignored);
    public static readonly KeyResult PassThrough = new(KeyResultKind.PassThrough);

    public static implicit operator KeyResult(KeyAction action) => new(KeyResultKind.Action, action);
}

/// <summary>
/// Maps keys to <see cref="InputAction"/>s depending on the <see cref="InputMode"/>. It keeps the keys of an unfinished
/// sequence (a count, <c>g</c> of <c>gg</c>, <c>d</c> of <c>dd</c>) between presses, any other key discards them.
/// </summary>
public sealed class VimKeymap
{
    private readonly StringBuilder _pending = new();
    private int _count;

    /// <summary>
    /// The keys of the unfinished sequence, shown in the status line like vim's <c>showcmd</c>.
    /// </summary>
    public string PendingKeys =>
        (_count > 0 ? _count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "") + _pending;

    public KeyResult Map(InputMode mode, Key key) =>
        mode switch
        {
            InputMode.Normal => MapNormal(key),
            InputMode.Insert => MapInsert(key),
            _ => MapLine(key),
        };

    /// <summary>
    /// Discards an unfinished sequence, for example when the mode changes.
    /// </summary>
    public void Reset()
    {
        _pending.Clear();
        _count = 0;
    }

    private static KeyResult MapInsert(Key key)
    {
        if (key == Key.Esc)
        {
            return new KeyAction(InputAction.Normal);
        }
        if (key == Key.Enter)
        {
            return new KeyAction(InputAction.Send);
        }
        // terminals can't tell Shift+Enter from Enter, Alt+Enter adds a line break
        if (key == Key.Enter.WithAlt)
        {
            return new KeyAction(InputAction.NewLine);
        }
        return KeyResult.PassThrough;
    }

    private static KeyResult MapLine(Key key)
    {
        if (key == Key.Esc)
        {
            return new KeyAction(InputAction.Cancel);
        }
        if (key == Key.Enter)
        {
            return new KeyAction(InputAction.Execute);
        }
        if (key == Key.CursorUp)
        {
            return new KeyAction(InputAction.HistoryPrevious);
        }
        if (key == Key.CursorDown)
        {
            return new KeyAction(InputAction.HistoryNext);
        }
        if (key == Key.Tab)
        {
            return new KeyAction(InputAction.Complete);
        }
        return KeyResult.PassThrough;
    }

    private KeyResult MapNormal(Key key)
    {
        if (key == Key.Esc)
        {
            Reset();
            return new KeyAction(InputAction.Cancel);
        }

        if (key.IsCtrl)
        {
            InputAction? ctrlAction = key.NoCtrl.KeyCode switch
            {
                Terminal.Gui.Drivers.KeyCode.D => InputAction.HalfPageDown,
                Terminal.Gui.Drivers.KeyCode.U => InputAction.HalfPageUp,
                Terminal.Gui.Drivers.KeyCode.F => InputAction.PageDown,
                Terminal.Gui.Drivers.KeyCode.B => InputAction.PageUp,
                Terminal.Gui.Drivers.KeyCode.N => InputAction.NextRoom,
                Terminal.Gui.Drivers.KeyCode.P => InputAction.PreviousRoom,
                Terminal.Gui.Drivers.KeyCode.Q => InputAction.Quit,
                _ => null,
            };
            return Finish(ctrlAction);
        }

        InputAction? special = SpecialKey(key);
        if (special is not null)
        {
            return Finish(special);
        }

        if (key.IsAlt || !key.TryGetPrintableRune(out Rune rune) || rune.Utf16SequenceLength != 1)
        {
            return Finish(null);
        }
        char c = (char)rune.Value;

        if (_pending.Length == 0 && char.IsAsciiDigit(c) && (c != '0' || _count > 0))
        {
            _count = Math.Min(_count * 10 + (c - '0'), 9999);
            return KeyResult.Pending;
        }

        _pending.Append(c);
        InputAction? action = _pending.ToString() switch
        {
            "j" => InputAction.MoveDown,
            "k" => InputAction.MoveUp,
            "gg" => InputAction.MoveFirst,
            "G" => InputAction.MoveLast,
            "h" => InputAction.FocusRooms,
            "l" => InputAction.FocusTimeline,
            "J" => InputAction.NextRoom,
            "K" => InputAction.PreviousRoom,
            "U" => InputAction.NextUnreadRoom,
            "i" => InputAction.Insert,
            "a" => InputAction.Append,
            ":" => InputAction.Command,
            "/" => InputAction.Filter,
            "r" => InputAction.Reply,
            "e" => InputAction.Edit,
            "dd" => InputAction.Delete,
            "+" => InputAction.React,
            "yy" => InputAction.Yank,
            "o" => InputAction.OpenMedia,
            "m" => InputAction.Members,
            "?" => InputAction.Help,
            "ZZ" or "ZQ" => InputAction.Quit,
            _ => null,
        };
        if (action is null && _pending.ToString() is "g" or "d" or "y" or "Z")
        {
            return KeyResult.Pending;
        }
        return Finish(action);
    }

    private static InputAction? SpecialKey(Key key)
    {
        if (key == Key.CursorDown)
        {
            return InputAction.MoveDown;
        }
        if (key == Key.CursorUp)
        {
            return InputAction.MoveUp;
        }
        if (key == Key.CursorLeft)
        {
            return InputAction.FocusRooms;
        }
        if (key == Key.CursorRight)
        {
            return InputAction.FocusTimeline;
        }
        if (key == Key.Home)
        {
            return InputAction.MoveFirst;
        }
        if (key == Key.End)
        {
            return InputAction.MoveLast;
        }
        if (key == Key.PageDown)
        {
            return InputAction.PageDown;
        }
        if (key == Key.PageUp)
        {
            return InputAction.PageUp;
        }
        if (key == Key.Tab)
        {
            return InputAction.ToggleFocus;
        }
        if (key == Key.Enter)
        {
            return InputAction.Open;
        }
        return null;
    }

    private KeyResult Finish(InputAction? action)
    {
        int count = Math.Max(_count, 1);
        Reset();
        return action is { } a ? new KeyAction(a, count) : KeyResult.Ignored;
    }
}

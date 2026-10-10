using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Matrix.RustSdk.Examples.TuiClient.Rendering;

/// <summary>
/// Shows <see cref="Row"/>s with one selected item, scrolled so the selection is visible. The rows are laid out for
/// the width of the view by the owner (the layout passed to <see cref="SetItems"/>), which is called again whenever the width changes.
/// Keys are handled by the window (<see cref="Input.VimKeymap"/>), the view only offers the movements.
/// </summary>
public sealed class RowsView : View
{
    private IReadOnlyList<Row> _rows = [];
    private int _itemCount;
    private int _top;
    private int _laidOutWidth = -1;
    private Func<int, IReadOnlyList<Row>> _layout = _ => [];

    public RowsView()
    {
        CanFocus = true;
        ViewportChanged += (_, _) =>
        {
            if (Viewport.Width != _laidOutWidth)
            {
                Relayout();
            }
        };
    }

    /// <summary>
    /// The selected item, null if there are no items.
    /// </summary>
    public int? SelectedItem { get; private set; }

    /// <summary>
    /// Whether the selection sticks to the last item when items are added, like a chat that follows new messages.
    /// Moving the selection away from the last item stops following, moving it back starts again.
    /// </summary>
    public bool FollowsEnd { get; init; }

    /// <summary>
    /// Shown when there are no rows.
    /// </summary>
    public string Placeholder { get; set; } = "";

    /// <summary>
    /// Whether the selection is shown when the view doesn't have the focus.
    /// </summary>
    public bool ShowsSelectionWithoutFocus { get; init; } = true;

    /// <summary>
    /// Raised after the selection moved.
    /// </summary>
    public event EventHandler? SelectionChanged;

    /// <summary>
    /// Raised when an item was clicked, after it was selected.
    /// </summary>
    public event EventHandler? ItemClicked;

    /// <summary>
    /// Raised when an item was double clicked, after it was selected.
    /// </summary>
    public event EventHandler? ItemActivated;

    /// <summary>
    /// The visible rows as text, for tests.
    /// </summary>
    public IReadOnlyList<Row> Rows => _rows;

    public int PageItems => Math.Max(1, Viewport.Height);

    /// <summary>
    /// Sets the items: <paramref name="layout"/> renders all of them for a width, <paramref name="itemCount"/> is their
    /// number and <paramref name="selectedItem"/> the item to select (the owner maps the previous selection to the new
    /// items). Without it the selection keeps its index, or follows the end (<see cref="FollowsEnd"/>).
    /// </summary>
    public void SetItems(int itemCount, Func<int, IReadOnlyList<Row>> layout, int? selectedItem = null)
    {
        bool following = FollowsEnd && (SelectedItem is null || SelectedItem == _itemCount - 1);
        _itemCount = itemCount;
        _layout = layout;
        int? previous = SelectedItem;
        if (itemCount == 0)
        {
            SelectedItem = null;
        }
        else if (following && selectedItem is null)
        {
            SelectedItem = itemCount - 1;
        }
        else
        {
            SelectedItem = Math.Clamp(selectedItem ?? SelectedItem ?? 0, 0, itemCount - 1);
        }
        Relayout();
        if (previous != SelectedItem)
        {
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void Select(int item)
    {
        if (_itemCount == 0)
        {
            return;
        }
        int clamped = Math.Clamp(item, 0, _itemCount - 1);
        if (clamped == SelectedItem)
        {
            return;
        }
        SelectedItem = clamped;
        EnsureSelectionVisible();
        SetNeedsDraw();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void MoveBy(int items) => Select((SelectedItem ?? 0) + items);

    public void MoveFirst() => Select(0);

    public void MoveLast() => Select(_itemCount - 1);

    /// <summary>
    /// Moves the selection by a number of screen lines, for page and half page movements.
    /// </summary>
    public void MoveByRows(int rows)
    {
        if (SelectedItem is not { } selected || _rows.Count == 0)
        {
            return;
        }
        int row = FirstRowOf(selected);
        int target = Math.Clamp(row + rows, 0, _rows.Count - 1);
        int item = _rows[target].Item;
        // a movement always moves, even if the item takes more lines than the movement
        if (item == selected)
        {
            item += Math.Sign(rows);
        }
        Select(item);
    }

    private void Relayout()
    {
        _laidOutWidth = Viewport.Width;
        _rows = _laidOutWidth > 0 ? _layout(_laidOutWidth) : [];
        EnsureSelectionVisible();
        SetNeedsDraw();
    }

    private int FirstRowOf(int item)
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            if (_rows[i].Item == item)
            {
                return i;
            }
        }
        return 0;
    }

    private void EnsureSelectionVisible()
    {
        int height = Viewport.Height;
        if (height <= 0)
        {
            return;
        }
        if (SelectedItem is not { } selected || _rows.Count == 0)
        {
            _top = 0;
            return;
        }
        int first = FirstRowOf(selected);
        int last = first;
        while (last + 1 < _rows.Count && _rows[last + 1].Item == selected)
        {
            last++;
        }
        if (FollowsEnd && selected == _itemCount - 1)
        {
            // the newest message sits at the bottom
            _top = Math.Max(0, _rows.Count - height);
            return;
        }
        if (first < _top)
        {
            _top = first;
        }
        else if (last >= _top + height)
        {
            // an item taller than the view shows its start
            _top = last - first + 1 > height ? first : last - height + 1;
        }
        _top = Math.Clamp(_top, 0, Math.Max(0, _rows.Count - height));
    }

    /// <summary>
    /// The wheel scrolls three lines, a click selects the item under the mouse, a double click activates it.
    /// </summary>
    protected override bool OnMouseEvent(Mouse mouse)
    {
        if (mouse.Flags.HasFlag(MouseFlags.WheeledDown))
        {
            MoveByRows(3);
            return true;
        }
        if (mouse.Flags.HasFlag(MouseFlags.WheeledUp))
        {
            MoveByRows(-3);
            return true;
        }
        if (mouse.IsSingleDoubleOrTripleClicked && mouse.Position is { } position)
        {
            int row = _top + position.Y;
            if (row >= 0 && row < _rows.Count)
            {
                Select(_rows[row].Item);
            }
            if (mouse.IsDoubleClicked)
            {
                ItemActivated?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                ItemClicked?.Invoke(this, EventArgs.Empty);
            }
            return true;
        }
        return base.OnMouseEvent(mouse);
    }

    protected override bool OnDrawingContent(DrawContext? context)
    {
        Attribute normal = GetAttributeForRole(VisualRole.Normal);
        int width = Viewport.Width;
        int height = Viewport.Height;
        bool showSelection = HasFocus || ShowsSelectionWithoutFocus;

        for (int y = 0; y < height; y++)
        {
            Move(0, y);
            int index = _top + y;
            int used = 0;
            if (index < _rows.Count)
            {
                Row row = _rows[index];
                bool selected = showSelection && row.Item == SelectedItem;
                foreach (Span span in row.Spans)
                {
                    string text = TextLayout.Truncate(span.Text, width - used);
                    if (text.Length == 0)
                    {
                        break;
                    }
                    SetAttribute(Theme.Resolve(span, normal, selected));
                    AddStr(text);
                    used += TextLayout.Width(text);
                }
                SetAttribute(selected ? Theme.Resolve(new Span(""), normal, true) : normal);
            }
            else if (_rows.Count == 0 && y == 0 && Placeholder.Length > 0)
            {
                string text = TextLayout.Truncate(Placeholder, width);
                SetAttribute(Theme.Resolve(new Span(text, Role.Dim), normal, false));
                AddStr(text);
                used = TextLayout.Width(text);
                SetAttribute(normal);
            }
            else
            {
                SetAttribute(normal);
            }
            if (used < width)
            {
                AddStr(new string(' ', width - used));
            }
        }
        return true;
    }
}

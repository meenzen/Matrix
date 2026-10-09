using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk;

/// <summary>
/// The filter and paging of a room list, passed to <see cref="RoomListExtensions.WatchEntryDiffsAsync"/>. Changes
/// apply to the running enumeration right away, a new enumeration starts with <see cref="Filter"/> and one page.
/// </summary>
/// <remarks>
/// A query is used by at most one enumeration at a time. Its members can be called from any thread.
/// </remarks>
public sealed class RoomListQuery
{
    private readonly Lock _lock = new();
    private RoomListEntriesDynamicFilterKind _filter = new RoomListEntriesDynamicFilterKind.NonLeft();
    private bool _inUse;
    private RoomListDynamicEntriesController? _controller;

    /// <summary>
    /// Creates a query yielding <paramref name="pageSize"/> rooms per page, by default all rooms that weren't left.
    /// </summary>
    /// <param name="pageSize">How many rooms a page contains, the enumeration starts with one page.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="pageSize"/> is less than 1.</exception>
    public RoomListQuery(int pageSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        PageSize = pageSize;
    }

    /// <summary>
    /// How many rooms a page contains.
    /// </summary>
    public int PageSize { get; }

    /// <summary>
    /// Which rooms to yield, by default <see cref="RoomListEntriesDynamicFilterKind.NonLeft"/>. Changing it while an
    /// enumeration runs makes the SDK start over with a <see cref="VectorDiff{T}.Reset"/> of the matching rooms, which
    /// replaces every room of a list.
    /// </summary>
    public RoomListEntriesDynamicFilterKind Filter
    {
        get
        {
            lock (_lock)
            {
                return _filter;
            }
        }
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_lock)
            {
                _filter = value;
                _controller?.SetFilter(value);
            }
        }
    }

    /// <summary>
    /// Yields one more page of rooms in the running enumeration. Does nothing without one, and before the SDK knows how
    /// many rooms there are (<see cref="RoomListLoadingState.Loaded"/>, see
    /// <see cref="RoomListExtensions.WatchLoadingStateAsync"/>).
    /// </summary>
    public void AddOnePage()
    {
        lock (_lock)
        {
            _controller?.AddOnePage();
        }
    }

    /// <summary>
    /// Goes back to the first page in the running enumeration. Does nothing without one.
    /// </summary>
    public void ResetToOnePage()
    {
        lock (_lock)
        {
            _controller?.ResetToOnePage();
        }
    }

    /// <summary>
    /// Reserves the query for an enumeration that is about to subscribe.
    /// </summary>
    /// <exception cref="InvalidOperationException">Another enumeration uses the query.</exception>
    internal void Acquire()
    {
        lock (_lock)
        {
            if (_inUse)
            {
                throw new InvalidOperationException(
                    "The query is used by another enumeration, every room list enumeration needs a query of its own."
                );
            }
            _inUse = true;
        }
    }

    /// <summary>
    /// Connects the query to the controller of the subscription and applies the filter, the SDK doesn't yield rooms
    /// before.
    /// </summary>
    internal void Attach(RoomListDynamicEntriesController controller)
    {
        lock (_lock)
        {
            _controller = controller;
            controller.SetFilter(_filter);
        }
    }

    /// <summary>
    /// Disconnects the query from its enumeration, which ended.
    /// </summary>
    internal void Release()
    {
        lock (_lock)
        {
            _controller = null;
            _inUse = false;
        }
    }
}

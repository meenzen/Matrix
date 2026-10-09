using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk;

/// <summary>
/// The filter and paging of a room list, passed to <see cref="RoomListExtensions.WatchRoomDiffsAsync"/>. Changes
/// apply to the running enumeration right away, a new enumeration starts with the current filter and one page.
/// </summary>
/// <remarks>
/// A query is used by at most one enumeration at a time. Its members can be called from any thread.
/// </remarks>
public sealed class RoomListQuery
{
    /// <summary>
    /// The default <see cref="BaseFilter"/>, the one Element X uses: rooms that weren't left and aren't spaces, invites
    /// to spaces, and only the current version of upgraded rooms.
    /// </summary>
    private static readonly RoomListEntriesDynamicFilterKind DefaultBaseFilter =
        new RoomListEntriesDynamicFilterKind.All([
            new RoomListEntriesDynamicFilterKind.Any([
                new RoomListEntriesDynamicFilterKind.All([
                    new RoomListEntriesDynamicFilterKind.NonSpace(),
                    new RoomListEntriesDynamicFilterKind.NonLeft(),
                ]),
                new RoomListEntriesDynamicFilterKind.All([
                    new RoomListEntriesDynamicFilterKind.Space(),
                    new RoomListEntriesDynamicFilterKind.Invite(),
                ]),
            ]),
            new RoomListEntriesDynamicFilterKind.DeduplicateVersions(),
        ]);

    private readonly Lock _lock = new();
    private readonly RoomListEntriesDynamicFilterKind _baseFilter = DefaultBaseFilter;
    private RoomListEntriesDynamicFilterKind? _filter;
    private bool _inUse;
    private RoomListDynamicEntriesController? _controller;

    /// <summary>
    /// Creates a query yielding <paramref name="pageSize"/> rooms per page.
    /// </summary>
    /// <param name="pageSize">
    /// How many rooms a page contains, the enumeration starts with one page. Element X shows 20 rooms per page and
    /// loads three more pages when the user scrolls close to the end.
    /// </param>
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
    /// The rooms the list contains at most, <see cref="Filter"/> narrows them down. By default the rooms Element X
    /// shows: rooms that weren't left and aren't spaces, invites to spaces, and only the current version of upgraded
    /// rooms. <c>new RoomListEntriesDynamicFilterKind.All([])</c> contains every room.
    /// </summary>
    public RoomListEntriesDynamicFilterKind BaseFilter
    {
        get => _baseFilter;
        init => _baseFilter = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>
    /// Narrows the rooms of <see cref="BaseFilter"/> down, for example to
    /// <see cref="RoomListEntriesDynamicFilterKind.Favourite"/> or
    /// <see cref="RoomListEntriesDynamicFilterKind.NormalizedMatchRoomName"/>, <see langword="null"/> (the default)
    /// for all of them. Changing it while an enumeration runs goes back to one page and makes the SDK start over with a
    /// <see cref="VectorDiff{T}.Reset"/> of the matching rooms, which replaces every item of a list.
    /// </summary>
    public RoomListEntriesDynamicFilterKind? Filter
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
            lock (_lock)
            {
                _filter = value;
                if (_controller is not null)
                {
                    _controller.ResetToOnePage();
                    _controller.SetFilter(EffectiveFilter());
                }
            }
        }
    }

    /// <summary>
    /// Yields one more page of rooms in the running enumeration. Does nothing without one (also right after
    /// <c>ToLiveList</c>, which starts the enumeration in the background), and before the SDK knows how many rooms there
    /// are. More rooms are available while the number of rooms is less than
    /// <see cref="RoomListLoadingState.Loaded.MaximumNumberOfRooms"/>, see
    /// <see cref="RoomListExtensions.WatchLoadingStateAsync"/>.
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
            controller.SetFilter(EffectiveFilter());
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

    private RoomListEntriesDynamicFilterKind EffectiveFilter() =>
        _filter is null ? _baseFilter : new RoomListEntriesDynamicFilterKind.All([_baseFilter, _filter]);
}

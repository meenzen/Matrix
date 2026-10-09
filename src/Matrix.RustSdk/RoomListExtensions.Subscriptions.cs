using Matrix.RustSdk.Subscriptions;

namespace Matrix.RustSdk.Bindings;

/// <summary>
/// Helpers for <see cref="RoomList"/>.
/// </summary>
public static partial class RoomListExtensions
{
    /// <summary>
    /// Watches the rooms of the list matching the filter of <paramref name="query"/>, sorted by recency like the SDK
    /// sorts them. Yields the changes in batches, starting with a <see cref="VectorDiff{T}.Reset"/> containing the first
    /// page. Applying them to a list in order keeps a copy of the rooms, see <c>ToLiveList</c>.
    /// </summary>
    /// <param name="roomList">The room list, usually <c>RoomListService.AllRooms()</c>.</param>
    /// <param name="query">
    /// The filter and paging, changes apply to the running enumeration. A query is used by one enumeration at a time.
    /// </param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/diffs/*"/>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/disposable/*"/>
    /// <para>
    /// The SDK replaces a room with a new <see cref="Room"/> (<see cref="VectorDiff{T}.Set"/>) whenever something
    /// notable changes, a new message or unread counts, and moves it with <see cref="VectorDiff{T}.Remove"/> and
    /// <see cref="VectorDiff{T}.Insert"/> when the order changes: keep the room id instead of the object. The first
    /// <see cref="VectorDiff{T}.Reset"/> contains the rooms known so far, it can be empty before the first sync,
    /// <see cref="WatchLoadingStateAsync"/> tells when the list is loaded.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// Another enumeration uses <paramref name="query"/>, thrown when the enumeration starts (in a <c>LiveList</c>:
    /// its <c>Completion</c> fails).
    /// </exception>
    public static IAsyncEnumerable<VectorDiff<Room>[]> WatchRoomDiffsAsync(
        this RoomList roomList,
        RoomListQuery query,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(roomList);
        ArgumentNullException.ThrowIfNull(query);
        return SubscriptionStream.CreateAsync<VectorDiff<Room>[], EntriesSubscription>(
            writer => new ValueTask<EntriesSubscription>(EntriesSubscription.Start(roomList, query, writer)),
            SubscriptionBuffer.All,
            cancellationToken: cancellationToken
        );
    }

    /// <summary>
    /// Watches whether the list is loaded. Yields its state, starting with the current one.
    /// <see cref="RoomListLoadingState.Loaded"/> contains the number of rooms the server knows about, if it sent it.
    /// </summary>
    /// <param name="roomList">The room list.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/></remarks>
    public static IAsyncEnumerable<RoomListLoadingState> WatchLoadingStateAsync(
        this RoomList roomList,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(roomList);
        return SubscriptionStream.CreateAsync<RoomListLoadingState, TaskHandle>(
            writer =>
            {
                RoomListLoadingStateResult result = roomList.LoadingState(new LoadingStateListener(writer));
                // the SDK returns the current state with the subscription, the listener only gets changes
                writer.WriteCurrent(result.State);
                return new ValueTask<TaskHandle>(result.StateStream);
            },
            SubscriptionBuffer.Latest,
            cancellationToken: cancellationToken
        );
    }

    [VectorDiffConversion]
    private static partial VectorDiff<Room> ToVectorDiff(this RoomListEntriesUpdate diff);

    private sealed class LoadingStateListener(SubscriptionWriter<RoomListLoadingState> writer)
        : RoomListLoadingStateListener
    {
        public void OnUpdate(RoomListLoadingState state) => writer.Write(state);
    }

    private sealed class EntriesListener(SubscriptionWriter<VectorDiff<Room>[]> writer) : RoomListEntriesListener
    {
        public void OnUpdate(RoomListEntriesUpdate[] roomEntriesUpdate) =>
            writer.Write(Array.ConvertAll(roomEntriesUpdate, ToVectorDiff));
    }

    /// <summary>
    /// The subscription of <see cref="RoomList.EntriesWithDynamicAdapters"/> and the query controlling it.
    /// </summary>
    private sealed class EntriesSubscription : ITaskHandle, IDisposable
    {
        private readonly RoomListQuery _query;
        private readonly RoomListEntriesWithDynamicAdaptersResult _result;
        private readonly RoomListDynamicEntriesController _controller;
        private readonly TaskHandle _handle;

        private EntriesSubscription(
            RoomListQuery query,
            RoomListEntriesWithDynamicAdaptersResult result,
            RoomListDynamicEntriesController controller,
            TaskHandle handle
        )
        {
            _query = query;
            _result = result;
            _controller = controller;
            _handle = handle;
        }

        public static EntriesSubscription Start(
            RoomList roomList,
            RoomListQuery query,
            SubscriptionWriter<VectorDiff<Room>[]> writer
        )
        {
            query.Acquire();
            RoomListEntriesWithDynamicAdaptersResult? result = null;
            RoomListDynamicEntriesController? controller = null;
            TaskHandle? handle = null;
            try
            {
                result = roomList.EntriesWithDynamicAdapters((uint)query.PageSize, new EntriesListener(writer));
                controller = result.Controller();
                handle = result.EntriesStream();
                // the SDK doesn't yield rooms before a filter is set
                query.Attach(controller);
                return new EntriesSubscription(query, result, controller, handle);
            }
            catch
            {
                query.Release();
                handle?.Dispose();
                controller?.Dispose();
                result?.Dispose();
                throw;
            }
        }

        public void Cancel() => _handle.Cancel();

        public bool IsFinished() => _handle.IsFinished();

        public void Dispose()
        {
            _query.Release();
            _handle.Dispose();
            _controller.Dispose();
            _result.Dispose();
        }
    }
}

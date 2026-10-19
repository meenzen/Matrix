using System.Runtime.CompilerServices;
using Matrix.RustSdk.Subscriptions;

namespace Matrix.RustSdk.Bindings;

/// <summary>
/// Helpers for <see cref="RoomListService"/>.
/// </summary>
public static partial class RoomListServiceExtensions
{
    /// <summary>
    /// Watches the state of the room list service. Yields it, starting with the current one.
    /// </summary>
    /// <param name="roomListService">The room list service.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/></remarks>
    [Subscription(nameof(RoomListService.State), SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<RoomListServiceState> WatchStateAsync(
        this RoomListService roomListService,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches whether a sync indicator should be shown. Yields it, starting with
    /// <see cref="RoomListServiceSyncIndicator.Hide"/>. Changes are delayed by <paramref name="delayBeforeShowing"/>
    /// and <paramref name="delayBeforeHiding"/> to avoid flickering.
    /// </summary>
    /// <param name="roomListService">The room list service.</param>
    /// <param name="delayBeforeShowing">How long the SDK has to be syncing before the indicator is shown.</param>
    /// <param name="delayBeforeHiding">How long the SDK has to be done before the indicator is hidden.</param>
    /// <include file="Subscriptions/Subscriptions.xml" path="docs/cancellationToken/*"/>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/></remarks>
    /// <exception cref="ArgumentOutOfRangeException">
    /// A delay is negative or longer than <see cref="uint.MaxValue"/> milliseconds.
    /// </exception>
    public static IAsyncEnumerable<RoomListServiceSyncIndicator> WatchSyncIndicatorAsync(
        this RoomListService roomListService,
        TimeSpan delayBeforeShowing,
        TimeSpan delayBeforeHiding,
        CancellationToken cancellationToken = default
    ) =>
        roomListService.WatchSyncIndicatorAsync(
            ToMilliseconds(delayBeforeShowing),
            ToMilliseconds(delayBeforeHiding),
            cancellationToken
        );

    // the bindings take the delays in milliseconds, the public overload takes TimeSpans
    [Subscription(nameof(RoomListService.SyncIndicator), SubscriptionBuffer.Latest)]
    internal static partial IAsyncEnumerable<RoomListServiceSyncIndicator> WatchSyncIndicatorAsync(
        this RoomListService roomListService,
        uint delayBeforeShowingInMs,
        uint delayBeforeHidingInMs,
        CancellationToken cancellationToken
    );

    private static uint ToMilliseconds(
        TimeSpan delay,
        [CallerArgumentExpression(nameof(delay))] string? parameterName = null
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero, parameterName);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(delay, TimeSpan.FromMilliseconds(uint.MaxValue), parameterName);
        return (uint)delay.TotalMilliseconds;
    }
}

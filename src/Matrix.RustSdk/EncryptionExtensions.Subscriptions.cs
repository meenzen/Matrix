using Matrix.RustSdk.Subscriptions;

namespace Matrix.RustSdk.Bindings;

/// <summary>
/// Helpers for <see cref="Encryption"/>.
/// </summary>
public static partial class EncryptionExtensions
{
    /// <summary>
    /// Watches the state of the key backup. Yields it, starting with the current one.
    /// </summary>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/></remarks>
    [Subscription(nameof(Encryption.BackupStateListener), SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<BackupState> WatchBackupStateAsync(
        this Encryption encryption,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the dehydrated device. Yields its events: created, uploaded, the progress of rehydrating it.
    /// </summary>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/stream/*"/></remarks>
    [Subscription(nameof(Encryption.DehydratedDeviceEventListener), SubscriptionBuffer.All)]
    public static partial IAsyncEnumerable<DehydratedDeviceEvent> WatchDehydratedDeviceEventsAsync(
        this Encryption encryption,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches the state of recovery. Yields it, starting with the current one.
    /// </summary>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/></remarks>
    [Subscription(nameof(Encryption.RecoveryStateListener), SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<RecoveryState> WatchRecoveryStateAsync(
        this Encryption encryption,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// Watches whether this device is verified. Yields the verification state, starting with the current one.
    /// </summary>
    /// <remarks><include file="Subscriptions/Subscriptions.xml" path="docs/state/*"/></remarks>
    [Subscription(nameof(Encryption.VerificationStateListener), SubscriptionBuffer.Latest)]
    public static partial IAsyncEnumerable<VerificationState> WatchVerificationStateAsync(
        this Encryption encryption,
        CancellationToken cancellationToken = default
    );
}

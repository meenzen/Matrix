using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk.Examples.TuiClient.Security;

/// <summary>
/// The encryption state of the session: whether it is verified, the state of the key backup and of recovery (secret
/// storage with a recovery key). Changes are reported with <see cref="Changed"/> on a thread pool thread.
/// </summary>
public sealed class EncryptionState(Client client)
{
    private readonly Encryption _encryption = client.Encryption();

    public VerificationState Verification { get; private set; } = VerificationState.Unknown;

    public BackupState Backup { get; private set; } = BackupState.Unknown;

    public RecoveryState Recovery { get; private set; } = RecoveryState.Unknown;

    public event EventHandler? Changed;

    /// <summary>
    /// A short summary for the status line.
    /// </summary>
    public string Summary =>
        string.Join(
                " ",
                Verification switch
                {
                    VerificationState.Verified => "verified",
                    VerificationState.Unverified => "unverified",
                    _ => "",
                },
                Backup switch
                {
                    BackupState.Enabled => "backup:on",
                    BackupState.Creating or BackupState.Enabling or BackupState.Resuming => "backup:enabling",
                    BackupState.Downloading => "backup:downloading",
                    _ => "backup:off",
                },
                Recovery switch
                {
                    RecoveryState.Enabled => "recovery:on",
                    RecoveryState.Incomplete => "recovery:incomplete",
                    RecoveryState.Disabled => "recovery:off",
                    _ => "",
                }
            )
            .Trim();

    /// <summary>
    /// Watches the states until <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    internal Task StartAsync(CancellationToken cancellationToken) =>
        Task.WhenAll(
            WatchAsync(_encryption.WatchVerificationStateAsync(cancellationToken), s => Verification = s),
            WatchAsync(_encryption.WatchBackupStateAsync(cancellationToken), s => Backup = s),
            WatchAsync(_encryption.WatchRecoveryStateAsync(cancellationToken), s => Recovery = s)
        );

    /// <summary>
    /// Sets up recovery: creates a key backup if there is none and stores the cross-signing and backup keys on the
    /// server, encrypted with a new recovery key, which is returned. The user has to keep it.
    /// </summary>
    public Task<string> EnableRecoveryAsync(IProgress<EnableRecoveryProgress>? progress = null) =>
        _encryption.EnableRecoveryAsync(progress: progress);

    /// <summary>
    /// Gets the keys from the server with the recovery key: this session is verified afterwards and old messages can
    /// be decrypted with the keys from the backup.
    /// </summary>
    public Task RecoverAsync(string recoveryKey) => _encryption.Recover(recoveryKey.Trim());

    /// <summary>
    /// Replaces the recovery key, the old one stops working. Returns the new key.
    /// </summary>
    public Task<string> ResetRecoveryKeyAsync() => _encryption.ResetRecoveryKey();

    public Task<bool> BackupExistsOnServerAsync() => _encryption.BackupExistsOnServer();

    public Task<bool> HasDevicesToVerifyAgainstAsync() => _encryption.HasDevicesToVerifyAgainst();

    private async Task WatchAsync<T>(IAsyncEnumerable<T> states, Action<T> apply)
    {
        try
        {
            await foreach (T state in states)
            {
                apply(state);
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            // the session is disposed
        }
    }
}

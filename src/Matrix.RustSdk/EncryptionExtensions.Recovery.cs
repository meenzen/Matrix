namespace Matrix.RustSdk.Bindings;

public static partial class EncryptionExtensions
{
    /// <summary>
    /// Enables recovery: creates a key backup if there is none and stores the secrets of the account (cross signing
    /// keys, the backup key) on the server, encrypted with a new recovery key. Other devices of the account use the key
    /// to verify themselves and to read the history.
    /// </summary>
    /// <param name="encryption">The encryption of the client.</param>
    /// <param name="passphrase">
    /// A passphrase the recovery key is derived from, <see langword="null"/> for a random key.
    /// </param>
    /// <param name="waitForBackupsToUpload">
    /// Whether to wait until the room keys of this device are uploaded to the new key backup.
    /// </param>
    /// <include file="Progress.xml" path="docs/progress/*"/>
    /// <returns>
    /// The recovery key. It is the only way to recover the account without another device, show it to the user or
    /// store it safely, and keep it out of logs (the <see cref="EnableRecoveryProgress.Done"/> progress contains it
    /// too).
    /// </returns>
    /// <remarks>
    /// <para>
    /// Recovery is enabled even if the reporting fails, <see cref="Encryption.ResetRecoveryKey"/> replaces a recovery
    /// key that got lost.
    /// </para>
    /// <include file="Progress.xml" path="docs/remarks/*"/>
    /// </remarks>
    /// <exception cref="RecoveryException">
    /// Recovery couldn't be enabled, <see cref="RecoveryException.BackupExistsOnServer"/> when the account already has
    /// a key backup.
    /// </exception>
    public static Task<string> EnableRecoveryAsync(
        this Encryption encryption,
        string? passphrase = null,
        bool waitForBackupsToUpload = false,
        IProgress<EnableRecoveryProgress>? progress = null
    )
    {
        ArgumentNullException.ThrowIfNull(encryption);
        ProgressReporter<EnableRecoveryProgress> reporter = new(progress);
        return reporter.RunAsync(() =>
            encryption.EnableRecovery(waitForBackupsToUpload, passphrase, new RecoveryProgressListener(reporter))
        );
    }

    /// <summary>
    /// Waits until the room keys of this device are uploaded to the key backup.
    /// </summary>
    /// <param name="encryption">The encryption of the client.</param>
    /// <include file="Progress.xml" path="docs/progress/*"/>
    /// <remarks>
    /// <para>
    /// Call it before logging out or deleting the device, keys that weren't uploaded are lost with the device. The first
    /// progress is the last state of the backup, a <see cref="BackupUploadState.Done"/> of an earlier upload for
    /// example.
    /// </para>
    /// <include file="Progress.xml" path="docs/remarks/*"/>
    /// </remarks>
    /// <exception cref="SteadyStateException">
    /// The backup is disabled, the connection failed or the SDK couldn't keep up with the progress.
    /// </exception>
    public static Task WaitForBackupUploadSteadyStateAsync(
        this Encryption encryption,
        IProgress<BackupUploadState>? progress = null
    )
    {
        ArgumentNullException.ThrowIfNull(encryption);
        ProgressReporter<BackupUploadState> reporter = new(progress);
        return reporter.RunAsync(() =>
            encryption.WaitForBackupUploadSteadyState(progress is null ? null : new BackupProgressListener(reporter))
        );
    }

    private sealed class RecoveryProgressListener(ProgressReporter<EnableRecoveryProgress> reporter)
        : EnableRecoveryProgressListener
    {
        public void OnUpdate(EnableRecoveryProgress status) => reporter.Report(status);
    }

    private sealed class BackupProgressListener(ProgressReporter<BackupUploadState> reporter)
        : BackupSteadyStateListener
    {
        public void OnUpdate(BackupUploadState status) => reporter.Report(status);
    }
}

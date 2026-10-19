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
    /// Whether to wait until the room keys of this device are uploaded to the new key backup. A failed upload doesn't
    /// fail the call, it is only reported as <see cref="EnableRecoveryProgress.RoomKeyUploadError"/>,
    /// <see cref="WaitForBackupUploadSteadyStateAsync"/> throws when the upload fails. Like that method it stops at the
    /// result of an earlier upload of this process.
    /// </param>
    /// <include file="Progress.xml" path="docs/progress/*"/>
    /// <returns>
    /// The recovery key. It is the only way to recover the account without another device, show it to the user or
    /// store it safely, and keep it out of logs (the <see cref="EnableRecoveryProgress.Done"/> progress contains it
    /// too).
    /// </returns>
    /// <remarks>
    /// <para>
    /// <see cref="Encryption.RecoveryState"/> (or <see cref="WatchRecoveryStateAsync"/>) tells what the account needs:
    /// <see cref="RecoveryState.Disabled"/> this method, <see cref="RecoveryState.Incomplete"/> the recovery key of an
    /// earlier setup (<see cref="Encryption.Recover"/>).
    /// </para>
    /// <para>
    /// When <paramref name="progress"/> throws, the returned task throws that exception and the recovery key is lost,
    /// but recovery is enabled anyway: <see cref="Encryption.ResetRecoveryKey"/> creates a new key.
    /// </para>
    /// <include file="Progress.xml" path="docs/remarks/*"/>
    /// </remarks>
    /// <exception cref="RecoveryException">
    /// Recovery couldn't be enabled. <see cref="RecoveryException.BackupExistsOnServer"/> when the account has a key
    /// backup this device doesn't use, created by another device: recover with its recovery key
    /// (<see cref="Encryption.Recover"/>) or replace it (<see cref="Encryption.ResetRecoveryKey"/>).
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
    /// Call it before logging out or deleting the device, keys that weren't uploaded are lost with the device. Uploading
    /// many keys takes a while, <see cref="Task.WaitAsync(CancellationToken)"/> stops waiting but not the upload.
    /// </para>
    /// <para>
    /// The SDK starts with the last state of the backup and doesn't reset it: once an upload finished in this process,
    /// the method returns right away on its <see cref="BackupUploadState.Done"/> (or throws on its
    /// <see cref="BackupUploadState.Error"/>), even if keys arrived since. It triggers an upload anyway, the keys are
    /// uploaded shortly after, but it only reliably waits for the first upload after the client was built.
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

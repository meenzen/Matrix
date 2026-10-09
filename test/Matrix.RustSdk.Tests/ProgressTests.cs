using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Testing;

namespace Matrix.RustSdk.Tests;

/// <summary>
/// The helpers reporting progress against a real homeserver.
/// </summary>
[Category(Homeserver.Category)]
[ClassDataSource<Homeserver>(Shared = SharedType.PerTestSession)]
public class ProgressTests(Homeserver homeserver)
{
    // the SDK reports the progress from a separate task, nothing makes sure a report arrives before the upload
    // completes: a few MB keep the request open long enough
    private const int UploadSize = 4 * 1024 * 1024;

    [Test]
    public async Task UploadMedia_ShouldReportTheProgress()
    {
        // Arrange
        Client client = await homeserver.LoginAsync(await homeserver.CreateUserAsync("upload"));
        byte[] data = new byte[UploadSize];
        Random.Shared.NextBytes(data);
        RecordingProgress<TransmissionProgress> progress = new();

        // Act
        string uri = await client.UploadMediaAsync("application/octet-stream", data, progress);
        int reportedWhenCompleted = progress.Values.Count;

        // Assert
        await Assert.That(uri).StartsWith("mxc://");
        await Assert.That(progress.Values).IsNotEmpty();
        await Assert.That(progress.Values.All(value => value.Current <= value.Total)).IsTrue();
        await Assert
            .That(progress.Values.Zip(progress.Values.Skip(1)).All(pair => pair.First.Current <= pair.Second.Current))
            .IsTrue();
        // the SDK reports until the request is dropped, the helper stops at the completion
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        await Assert.That(progress.Values.Count).IsEqualTo(reportedWhenCompleted);
    }

    [Test]
    public async Task UploadMedia_ShouldThrowTheExceptionOfReportAfterUploading()
    {
        // Arrange
        Client client = await homeserver.LoginAsync(await homeserver.CreateUserAsync("upload"));
        byte[] data = new byte[UploadSize];

        // Act
        Task Upload() =>
            client.UploadMediaAsync(
                "application/octet-stream",
                data,
                new RecordingProgress<TransmissionProgress>(_ => throw new InvalidOperationException("report failed"))
            );

        // Assert
        await Assert.That(Upload).Throws<InvalidOperationException>().WithMessage("report failed");
    }

    [Test]
    public async Task EnableRecovery_ShouldReturnTheRecoveryKey()
    {
        // Arrange
        Client client = await homeserver.LoginAsync(await homeserver.CreateUserAsync("recovery"));
        using Encryption encryption = client.Encryption();
        RecordingProgress<EnableRecoveryProgress> progress = new();

        // Act
        string key = await encryption.EnableRecoveryAsync(progress: progress);

        // Assert
        await Assert.That(key).IsNotEmpty();
        await Assert.That(progress.Values).IsNotEmpty();
        await Assert.That(progress.Values[0]).IsTypeOf<EnableRecoveryProgress.Starting>();
        // the recovery state stays incomplete, the test users have no cross signing keys
        await Assert.That(await encryption.BackupExistsOnServer()).IsTrue();
    }

    [Test]
    public async Task WaitForBackupUploadSteadyState_ShouldCompleteWithABackup()
    {
        // Arrange
        Client client = await homeserver.LoginAsync(await homeserver.CreateUserAsync("backup"));
        using Encryption encryption = client.Encryption();
        await encryption.EnableRecoveryAsync();
        RecordingProgress<BackupUploadState> progress = new();

        // Act
        await encryption.WaitForBackupUploadSteadyStateAsync(progress);

        // Assert
        await Assert.That(progress.Values.OfType<BackupUploadState.Error>()).IsEmpty();
    }

    /// <summary>
    /// Records the reports synchronously, the SDK reports one at a time.
    /// </summary>
    private sealed class RecordingProgress<T>(Action<T>? report = null) : IProgress<T>
    {
        private readonly Lock _lock = new();
        private readonly List<T> _values = [];

        public List<T> Values
        {
            get
            {
                lock (_lock)
                {
                    return [.. _values];
                }
            }
        }

        public void Report(T value)
        {
            lock (_lock)
            {
                _values.Add(value);
            }
            report?.Invoke(value);
        }
    }
}

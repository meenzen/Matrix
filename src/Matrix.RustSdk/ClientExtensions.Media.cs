namespace Matrix.RustSdk.Bindings;

public static partial class ClientExtensions
{
    /// <summary>
    /// Uploads a file to the media repository of the homeserver and reports how much of it was sent.
    /// </summary>
    /// <param name="client">The client.</param>
    /// <param name="mimeType">The content type of the file, <c>image/png</c> for example.</param>
    /// <param name="data">The content of the file.</param>
    /// <include file="Progress.xml" path="docs/progress/*"/>
    /// <returns>The <c>mxc://</c> URI of the uploaded file.</returns>
    /// <remarks>
    /// <para>
    /// Upload the content to reference it in events, room avatars for example. The send helpers of
    /// <see cref="Timeline"/> (<see cref="Timeline.SendImage"/>, <see cref="Timeline.SendFile"/>, ...) upload the files
    /// they send themselves.
    /// </para>
    /// <include file="Progress.xml" path="docs/remarks/*"/>
    /// </remarks>
    /// <exception cref="ClientException">The upload failed.</exception>
    public static Task<string> UploadMediaAsync(
        this Client client,
        string mimeType,
        byte[] data,
        IProgress<TransmissionProgress>? progress = null
    )
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(mimeType);
        ArgumentNullException.ThrowIfNull(data);
        ProgressReporter<TransmissionProgress> reporter = new(progress);
        return reporter.RunAsync(() =>
            client.UploadMedia(mimeType, data, progress is null ? null : new UploadProgressListener(reporter))
        );
    }

    private sealed class UploadProgressListener(ProgressReporter<TransmissionProgress> reporter) : ProgressWatcher
    {
        public void TransmissionProgress(TransmissionProgress progress) => reporter.Report(progress);
    }
}

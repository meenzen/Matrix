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
    /// Upload content to reference it by its URI, in the content of custom events for example. The file is stored
    /// unencrypted: attachments, in encrypted rooms too, are sent with <see cref="Timeline.SendImage"/>,
    /// <see cref="Timeline.SendFile"/> and the other send methods of <see cref="Timeline"/>, which upload (and encrypt)
    /// the files themselves. Avatars are set with <see cref="Client.UploadAvatar"/> and <see cref="Room.UploadAvatar"/>.
    /// </para>
    /// <include file="Progress.xml" path="docs/remarks/*"/>
    /// </remarks>
    /// <exception cref="ClientException">The upload failed or <paramref name="mimeType"/> isn't a MIME type.</exception>
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

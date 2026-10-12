namespace Matrix.RustSdk.Examples.TuiClient.Chat;

/// <summary>
/// The MIME types of common file extensions, the SDK requires one for every upload.
/// </summary>
public static class MimeTypes
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".svg"] = "image/svg+xml",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".mov"] = "video/quicktime",
        [".mkv"] = "video/x-matroska",
        [".mp3"] = "audio/mpeg",
        [".ogg"] = "audio/ogg",
        [".opus"] = "audio/opus",
        [".wav"] = "audio/wav",
        [".flac"] = "audio/flac",
        [".m4a"] = "audio/mp4",
        [".pdf"] = "application/pdf",
        [".zip"] = "application/zip",
        [".gz"] = "application/gzip",
        [".tar"] = "application/x-tar",
        [".json"] = "application/json",
        [".txt"] = "text/plain",
        [".md"] = "text/markdown",
        [".csv"] = "text/csv",
        [".html"] = "text/html",
    };

    public static string ForPath(string path) =>
        ByExtension.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");

    /// <summary>
    /// <c>image</c>, <c>video</c>, <c>audio</c> or <c>file</c>, which decides how a file is sent.
    /// </summary>
    public static string Kind(string mimeType) =>
        mimeType.Split('/')[0] switch
        {
            "image" when mimeType != "image/svg+xml" => "image",
            "video" => "video",
            "audio" => "audio",
            _ => "file",
        };
}

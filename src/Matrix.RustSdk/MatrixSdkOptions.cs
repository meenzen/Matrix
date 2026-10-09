using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk;

/// <summary>
/// Options of <see cref="MatrixSdk.Initialize(MatrixSdkOptions?)"/>. The defaults log nothing.
/// </summary>
public sealed class MatrixSdkOptions
{
    /// <summary>
    /// The level of the logs of the SDK, <see cref="LogLevel.Warn"/> by default. Some components log more by default.
    /// </summary>
    public LogLevel LogLevel { get; init; } = LogLevel.Warn;

    /// <summary>
    /// Whether the SDK writes its logs to the standard output. Off by default, the logs would mix with the output of
    /// console apps.
    /// </summary>
    public bool LogToConsole { get; init; }

    /// <summary>
    /// The directory the SDK writes its logs to, a file per hour named <c>matrix-sdk.*.log</c>. It removes the oldest
    /// files when they take more than 10 MB together or are older than a week. Relative paths are resolved against the
    /// current directory. <see langword="null"/> (the default) writes no files.
    /// </summary>
    public string? LogDirectory { get; init; }

    /// <summary>
    /// Whether to use a runtime with at most four threads, for processes that have to save memory. By default the
    /// runtime has a thread per CPU core.
    /// </summary>
    public bool UseLightweightRuntime { get; init; }
}

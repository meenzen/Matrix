using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk;

/// <summary>
/// Process wide setup of matrix-rust-sdk: its logs and the runtime that runs its tasks.
/// </summary>
public static class MatrixSdk
{
    private static readonly Lock InitializeLock = new();

    /// <summary>
    /// Whether <see cref="Initialize(MatrixSdkOptions?)"/> was called successfully.
    /// </summary>
    public static bool IsInitialized { get; private set; }

    /// <summary>
    /// Sets up matrix-rust-sdk for this process. Call it once, at the start of the app before it creates a
    /// <see cref="ClientBuilder"/> or calls any other method of the bindings.
    /// </summary>
    /// <param name="options">The logs and the runtime, <see langword="null"/> for the defaults: no logs.</param>
    /// <remarks>
    /// <para>
    /// The SDK runs its tasks on a runtime of its own. Without this call it creates a runtime with a single thread when
    /// it needs one, so everything the SDK does in the background (syncing, sending, the subscriptions) shares one
    /// thread, and it logs nothing, not even panics. The runtime is created by the first asynchronous call into the
    /// SDK, calling this method afterwards still sets up the logs, but the runtime stays single threaded.
    /// </para>
    /// <para>
    /// The SDK writes its logs itself, to the standard output or to files, they can't be forwarded to an
    /// <c>ILogger</c>. It sets the environment variable <c>RUST_BACKTRACE</c>, so panics are logged with a backtrace,
    /// and writes a line to the standard error when it creates the runtime, even without logs.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The SDK was already initialized, by this method or by <see cref="MatrixSdkFfiMethods.InitPlatform"/>.
    /// </exception>
    public static void Initialize(MatrixSdkOptions? options = null)
    {
        options ??= new MatrixSdkOptions();
        TracingFileConfiguration? files = null;
        if (options.LogDirectory is { } directory)
        {
            directory = Path.GetFullPath(directory);
            // the SDK panics when it can't create the directory, this reports why
            Directory.CreateDirectory(directory);
            files = new TracingFileConfiguration(
                directory,
                FilePrefix: "matrix-sdk",
                FileSuffix: null,
                MaxTotalSizeBytes: null,
                MaxAgeSeconds: null
            );
        }
        Initialize(
            new TracingConfiguration(
                options.LogLevel,
                TraceLogPacks: [],
                ExtraTargets: [],
                WriteToStdoutOrSystem: options.LogToConsole,
                WriteToFiles: files
            ),
            options.UseLightweightRuntime
        );
    }

    /// <summary>
    /// Sets up matrix-rust-sdk for this process with a complete tracing configuration, see
    /// <see cref="Initialize(MatrixSdkOptions?)"/>.
    /// </summary>
    /// <param name="tracing">The logs: level, log packs of SDK components, extra targets, output.</param>
    /// <param name="useLightweightRuntime">
    /// Whether to use a runtime with at most four threads, for processes that have to save memory.
    /// </param>
    /// <inheritdoc cref="Initialize(MatrixSdkOptions?)" path="/remarks"/>
    /// <inheritdoc cref="Initialize(MatrixSdkOptions?)" path="/exception"/>
    public static void Initialize(TracingConfiguration tracing, bool useLightweightRuntime = false)
    {
        ArgumentNullException.ThrowIfNull(tracing);
        lock (InitializeLock)
        {
            if (IsInitialized)
            {
                throw new InvalidOperationException(
                    "matrix-rust-sdk was already initialized, call MatrixSdk.Initialize once per process."
                );
            }
            try
            {
                MatrixSdkFfiMethods.InitPlatform(tracing, useLightweightRuntime);
            }
            // the SDK panics when it sets up the logs a second time, InitPlatform was called directly then
            catch (PanicException e)
            {
                throw new InvalidOperationException(
                    "Initializing matrix-rust-sdk failed, it was probably initialized already by a direct call of "
                        + "MatrixSdkFfiMethods.InitPlatform.",
                    e
                );
            }
            IsInitialized = true;
        }
    }
}

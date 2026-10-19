namespace Matrix.RustSdk;

/// <summary>
/// The data directory of a <see cref="StoredClient"/> is used by another <see cref="StoredClient"/>, in this process
/// or another one, or by a cancelled login or restore that is still running. Two clients on one store would corrupt
/// its encryption state.
/// </summary>
#pragma warning disable RCS1194 // the HResult constructor of IOException doesn't apply
public sealed class DataDirectoryLockedException : IOException
{
    /// <summary>
    /// Creates the exception with a default message.
    /// </summary>
    public DataDirectoryLockedException()
        : base("The data directory is used by another client.") { }

    /// <summary>
    /// Creates the exception with <paramref name="message"/>.
    /// </summary>
    public DataDirectoryLockedException(string message)
        : base(message) { }

    /// <summary>
    /// Creates the exception with <paramref name="message"/>, caused by <paramref name="innerException"/>.
    /// </summary>
    public DataDirectoryLockedException(string message, Exception innerException)
        : base(message, innerException) { }
}
#pragma warning restore RCS1194

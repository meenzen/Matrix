using System.Text.Json;

namespace Matrix.RustSdk;

/// <summary>
/// The data directory of a <see cref="StoredClient"/>, locked exclusively from <see cref="Lock"/> until it is disposed.
/// Only the holder of the lock reads or changes the files.
/// </summary>
internal sealed class DataDirectory : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    // the lock file is never deleted: a process that deleted it and created it again would lock another file than a
    // process that still holds the old one
    private readonly FileStream _lock;

    private DataDirectory(string path, FileStream lockFile)
    {
        Path = path;
        _lock = lockFile;
    }

    public string Path { get; }

    /// <summary>
    /// The data path of the SDK: the state store and the crypto store with the keys of the device.
    /// </summary>
    public string StorePath => System.IO.Path.Join(Path, "store");

    /// <summary>
    /// The cache path of the SDK: the event cache and the media store.
    /// </summary>
    public string CachePath => System.IO.Path.Join(Path, "cache");

    private string SessionPath => System.IO.Path.Join(Path, "session.json");

    private string TemporarySessionPath => System.IO.Path.Join(Path, "session.json.tmp");

    /// <summary>
    /// Whether the SDK stored anything: a store without a session must not be used by another login.
    /// </summary>
    public bool HasStore => Directory.Exists(StorePath) && Directory.EnumerateFileSystemEntries(StorePath).Any();

    /// <summary>
    /// Creates the directory if needed (owner only on Unix) and locks it.
    /// </summary>
    /// <exception cref="DataDirectoryLockedException">Another client holds the lock.</exception>
    public static DataDirectory Lock(string path)
    {
        path = System.IO.Path.GetFullPath(path);
        CreateDirectory(path);

        FileStream lockFile;
        try
        {
            // FileShare.None is a share mode on Windows and an flock on Unix, both exclude other processes and other
            // streams in this process
            lockFile = new FileStream(
                System.IO.Path.Join(path, "lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None
            );
        }
        catch (IOException e) when (IsLockConflict(e))
        {
            throw new DataDirectoryLockedException(
                $"The data directory {path} is used by another client, in this process or another one, or by a "
                    + "cancelled login or restore that is still running.",
                e
            );
        }

        DataDirectory directory = new(path, lockFile);
        try
        {
            // left over by a write that didn't finish
            File.Delete(directory.TemporarySessionPath);
            // the SDK would create them readable for everyone with the default umask, the crypto store contains the
            // keys of the device
            CreateDirectory(directory.StorePath);
            CreateDirectory(directory.CachePath);
        }
        catch
        {
            directory.Dispose();
            throw;
        }
        return directory;
    }

    /// <summary>
    /// Reads <c>session.json</c>, <see langword="null"/> if there is none.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file can't be read.</exception>
    public SessionFile? ReadSession()
    {
        SessionFile? session;
        try
        {
            using FileStream stream = File.OpenRead(SessionPath);
            session = JsonSerializer.Deserialize(stream, SessionFileJsonContext.Default.SessionFile);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (JsonException e)
        {
            throw new InvalidOperationException(
                $"{SessionPath} can't be read. It belongs to the store in the same directory, restore it from a "
                    + "backup or delete the whole directory to log in with a new device.",
                e
            );
        }

        if (session is null || session.Version > SessionFile.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"{SessionPath} was written by a newer version of Matrix.RustSdk (format {session?.Version})."
            );
        }
        return session;
    }

    /// <summary>
    /// Replaces <c>session.json</c> atomically: a crash leaves either the old or the new file.
    /// </summary>
    public void WriteSession(SessionFile session)
    {
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
        };
        if (!OperatingSystem.IsWindows())
        {
            // it contains the access token
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        File.Delete(TemporarySessionPath);
        using (FileStream stream = new(TemporarySessionPath, options))
        {
            JsonSerializer.Serialize(stream, session, SessionFileJsonContext.Default.SessionFile);
            stream.Flush(flushToDisk: true);
        }
        File.Move(TemporarySessionPath, SessionPath, overwrite: true);
    }

    public void DeleteSession() => File.Delete(SessionPath);

    /// <summary>
    /// Deletes the stores of the SDK. The client using them has to be paused (<c>Client.Pause</c>) first.
    /// </summary>
    public void DeleteStore()
    {
        foreach (string path in new[] { StorePath, CachePath }.Where(Directory.Exists))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    public void Dispose() => _lock.Dispose();

    private static void CreateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
        }
        else
        {
            // the mode only applies to a new directory, an existing one keeps its permissions
            Directory.CreateDirectory(path, OwnerOnly);
        }
    }

    private static bool IsLockConflict(IOException exception) =>
        // EWOULDBLOCK of flock on Linux and macOS, sharing and lock violations on Windows
        exception.HResult
            is 11
                or 35
                or unchecked((int)0x80070020)
                or unchecked((int)0x80070021);
}

using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk;

/// <summary>
/// Where and how a <see cref="StoredClient"/> stores its session and its data. The properties can be bound from
/// configuration, except for the callbacks.
/// </summary>
public sealed class ClientStoreOptions
{
    /// <summary>
    /// The directory of one account: the session (<c>session.json</c>), the sqlite stores of the SDK (<c>store</c> with
    /// the encryption keys, <c>cache</c> with the event and media caches) and a lock file. Created if it doesn't exist,
    /// on Unix readable by the owner only. Relative paths are resolved against the current directory.
    /// </summary>
    /// <remarks>
    /// Use a directory per account and a local file system: the lock that keeps a second process away is an advisory
    /// lock on Unix (<c>flock</c>), it doesn't work on network file systems or with
    /// <c>System.IO.DisableFileLocking</c>.
    /// </remarks>
    public string DataDirectory { get; set; } = "";

    /// <summary>
    /// The passphrase that encrypts the sqlite stores, <see langword="null"/> (the default) stores them unencrypted. It
    /// can't be added or changed later. <c>session.json</c> isn't encrypted, it contains the access token and relies on
    /// the permissions of the file and the directory.
    /// </summary>
    public string? StorePassphrase { get; set; }

    /// <summary>
    /// Further settings of the client: proxy, user agent, request timeouts, the sliding sync version of a login. It is
    /// called for every client the helper builds, before the helper sets the homeserver and the store.
    /// </summary>
    /// <remarks>
    /// A login uses <see cref="SlidingSyncVersionBuilder.DiscoverNative"/> unless this callback sets another version,
    /// so the client can sync with <see cref="Client.SyncService"/>. It fails on homeservers without simplified sliding
    /// sync (MSC4186), clients that only use <see cref="Client.SyncOnceV2"/> can set
    /// <see cref="SlidingSyncVersionBuilder.None"/>. A restore uses the version stored with the session.
    /// </remarks>
    public Func<ClientBuilder, ClientBuilder>? ConfigureClient { get; set; }

    /// <summary>
    /// Receives the authentication errors and the background task errors of the client. A client accepts only one
    /// delegate and <see cref="StoredClient"/> sets its own to notice when the session ended, so
    /// <see cref="Client.SetDelegate"/> fails: set the delegate here instead. Apps that only need to know when the
    /// session ended use <see cref="StoredClient.SessionEnded"/>. It is called on threads of the SDK, exceptions are
    /// ignored.
    /// </summary>
    public ClientDelegate? ClientDelegate { get; set; }
}

using System.Security.Cryptography;
using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk;

/// <summary>
/// A <see cref="Bindings.Client"/> whose session and stores live in a data directory: it logs in with a password once
/// and restores the session on later starts. It holds an exclusive lock on the directory until it is disposed.
/// </summary>
/// <remarks>
/// <para>
/// The data directory belongs to one account and one device: the store contains the encryption keys of the device the
/// login created. The helper never deletes it on its own, only <see cref="LogoutAsync"/> does, or the next start after
/// the homeserver deleted the device. A login that crashed before it stored the session continues with the same device
/// on the next attempt. <c>session.json</c> contains the access token, on Unix only its owner can read it.
/// </para>
/// <para>
/// The <see cref="StoredClient"/> owns <see cref="Client"/>, don't dispose it. Stop the <see cref="SyncService"/> and
/// dispose the objects created from the client before disposing the <see cref="StoredClient"/>: disposing closes the
/// stores (<see cref="Client.Pause"/>) before it releases the lock, objects that still use the client fail afterwards.
/// </para>
/// <para>
/// The homeserver can end a session at any time (logout from another client, password change, an admin). The SDK
/// notices it at the next request, not when restoring, then <see cref="SessionEnded"/> is cancelled and the client
/// can't be used anymore: dispose it and log in again. After a soft logout the device still exists, the login continues
/// with the same device and store. Otherwise the device is gone, the next start deletes the store and the login after
/// it creates a new device.
/// </para>
/// <para>
/// The bindings can't cancel calls into the SDK. A cancelled login or restore stops waiting, the call continues in the
/// background and releases the data directory when it finished.
/// </para>
/// </remarks>
public sealed class StoredClient : IAsyncDisposable, IDisposable
{
    private const string DeviceIdCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const int DeviceIdLength = 10;

    private readonly DataDirectory _directory;
    private readonly OpenedClient _opened;
    private readonly SessionFile _session;
    private readonly Lock _closeLock = new();
    private Task? _logout;
    private Task? _closing;

    private StoredClient(DataDirectory directory, OpenedClient opened, SessionFile session, bool isRestored)
    {
        _directory = directory;
        _opened = opened;
        _session = session;
        IsRestored = isRestored;
    }

    private enum OpenMode
    {
        Restore,
        Login,
        LoginOrRestore,
    }

    /// <summary>
    /// The logged in client. Owned by this object: don't dispose it, and log out with <see cref="LogoutAsync"/> instead
    /// of <see cref="Client.Logout"/>, which would leave the session and the store of the deleted device behind.
    /// </summary>
    public Client Client => _opened.Client;

    /// <summary>
    /// Whether the stored session was restored. <see langword="false"/> after a login, which created a new device unless
    /// it continued a soft logged out session: apps set up the encryption of a new device, for example with
    /// <see cref="Encryption.Recover"/> or a verification.
    /// </summary>
    public bool IsRestored { get; }

    /// <summary>
    /// Cancelled when the homeserver ended the session (<c>M_UNKNOWN_TOKEN</c>): logged out by another client, the
    /// password changed, an admin, or a soft logout. The client can't be used anymore, dispose it and log in again.
    /// </summary>
    public CancellationToken SessionEnded => _opened.Tracker.SessionEnded;

    /// <summary>
    /// Restores the session stored in <see cref="ClientStoreOptions.DataDirectory"/>, without a request to the
    /// homeserver. Apps that ask for the password call it first and only show their login form when it returns
    /// <see langword="null"/>.
    /// </summary>
    /// <param name="options">The data directory and the settings of the client.</param>
    /// <param name="cancellationToken">
    /// Stops waiting, the restore continues in the background and releases the data directory when it finished.
    /// </param>
    /// <returns>
    /// The restored client, <see langword="null"/> if there is no session to restore: no login yet, the last login
    /// didn't finish, it was logged out, or the homeserver ended the session. If the homeserver deleted the device, the
    /// store is deleted too.
    /// </returns>
    /// <exception cref="ArgumentException"><see cref="ClientStoreOptions.DataDirectory"/> is missing.</exception>
    /// <exception cref="DataDirectoryLockedException">Another client uses the data directory.</exception>
    /// <exception cref="InvalidOperationException">
    /// <c>session.json</c> can't be read, or the store of the session is missing.
    /// </exception>
    /// <exception cref="ClientBuildException">
    /// The client can't be built, for example because <see cref="ClientStoreOptions.StorePassphrase"/> is wrong.
    /// </exception>
    /// <exception cref="ClientException">The store doesn't match the session.</exception>
    /// <exception cref="IOException">The data directory can't be created or changed.</exception>
    /// <exception cref="UnauthorizedAccessException">The data directory can't be created or changed.</exception>
    public static Task<StoredClient?> TryRestoreAsync(
        ClientStoreOptions options,
        CancellationToken cancellationToken = default
    )
    {
        Validate(options);
        return WaitAsync(() => OpenAsync(options, credentials: null, OpenMode.Restore), cancellationToken);
    }

    /// <summary>
    /// Logs in with a password and stores the session in <see cref="ClientStoreOptions.DataDirectory"/>.
    /// </summary>
    /// <param name="options">The data directory and the settings of the client.</param>
    /// <param name="credentials">The account, the password is required.</param>
    /// <param name="cancellationToken">
    /// Stops waiting, the login continues in the background and releases the data directory when it finished.
    /// </param>
    /// <returns>The logged in client.</returns>
    /// <exception cref="ArgumentException">
    /// <see cref="ClientStoreOptions.DataDirectory"/>, the homeserver, the username or the password is missing.
    /// </exception>
    /// <exception cref="DataDirectoryLockedException">Another client uses the data directory.</exception>
    /// <exception cref="InvalidOperationException">
    /// The data directory contains a session that can be restored, a session or a login of another account, a store
    /// without a session, or a <c>session.json</c> that can't be read.
    /// </exception>
    /// <exception cref="ClientBuildException">
    /// The client can't be built: the homeserver can't be found or doesn't support simplified sliding sync, or
    /// <see cref="ClientStoreOptions.StorePassphrase"/> is wrong.
    /// </exception>
    /// <exception cref="ClientException">
    /// The login failed, <see cref="ClientException.MatrixApi"/> with <see cref="ErrorKind.Forbidden"/> for a wrong
    /// username or password.
    /// </exception>
    /// <exception cref="IOException">The data directory can't be created or changed.</exception>
    /// <exception cref="UnauthorizedAccessException">The data directory can't be created or changed.</exception>
    public static Task<StoredClient> LoginAsync(
        ClientStoreOptions options,
        PasswordCredentials credentials,
        CancellationToken cancellationToken = default
    )
    {
        Validate(options);
        Validate(credentials);
        if (string.IsNullOrEmpty(credentials.Password))
        {
            throw new ArgumentException("Logging in needs a password.", nameof(credentials));
        }
        return WaitAsync(() => OpenAsync(options, credentials, OpenMode.Login), cancellationToken)!;
    }

    /// <summary>
    /// Restores the session stored in <see cref="ClientStoreOptions.DataDirectory"/>, or logs in with the password and
    /// stores the session if there is none. For bots and services, apps that ask for the password use
    /// <see cref="TryRestoreAsync"/> and <see cref="LoginAsync"/>.
    /// </summary>
    /// <param name="options">The data directory and the settings of the client.</param>
    /// <param name="credentials">
    /// The account. A stored session has to belong to it, the password is only needed when there is none.
    /// </param>
    /// <param name="cancellationToken">
    /// Stops waiting, the restore or login continues in the background and releases the data directory when it
    /// finished.
    /// </param>
    /// <returns>The restored or logged in client, see <see cref="IsRestored"/>.</returns>
    /// <exception cref="ArgumentException">
    /// <see cref="ClientStoreOptions.DataDirectory"/>, the homeserver or the username is missing, or there is no
    /// session to restore and no password.
    /// </exception>
    /// <exception cref="DataDirectoryLockedException">Another client uses the data directory.</exception>
    /// <exception cref="InvalidOperationException">
    /// The data directory contains a session or a login of another account, a store without a session, a session
    /// without its store, or a <c>session.json</c> that can't be read.
    /// </exception>
    /// <exception cref="ClientBuildException">
    /// The client can't be built: the homeserver can't be found or doesn't support simplified sliding sync, or
    /// <see cref="ClientStoreOptions.StorePassphrase"/> is wrong.
    /// </exception>
    /// <exception cref="ClientException">
    /// The restore or the login failed, <see cref="ClientException.MatrixApi"/> with <see cref="ErrorKind.Forbidden"/>
    /// for a wrong username or password.
    /// </exception>
    /// <exception cref="IOException">The data directory can't be created or changed.</exception>
    /// <exception cref="UnauthorizedAccessException">The data directory can't be created or changed.</exception>
    public static Task<StoredClient> LoginOrRestoreAsync(
        ClientStoreOptions options,
        PasswordCredentials credentials,
        CancellationToken cancellationToken = default
    )
    {
        Validate(options);
        Validate(credentials);
        return WaitAsync(() => OpenAsync(options, credentials, OpenMode.LoginOrRestore), cancellationToken)!;
    }

    /// <summary>
    /// Logs out, which deletes the device on the homeserver, deletes the session and the store and disposes this
    /// object. A session the homeserver ended already counts as logged out, after a soft logout the device stays on the
    /// homeserver then (remove it in another client).
    /// </summary>
    /// <remarks>
    /// It takes no <see cref="CancellationToken"/>: a logout that was abandoned could still reach the homeserver, the
    /// data directory would keep a session that doesn't work anymore. The request ends with the timeout of the SDK.
    /// </remarks>
    /// <exception cref="ClientException">
    /// The logout failed, for example because the homeserver can't be reached. Nothing was deleted, the client can still
    /// be used.
    /// </exception>
    /// <exception cref="IOException">
    /// Logged out, but the store couldn't be deleted (on Windows, objects created from the client that weren't disposed
    /// keep its files open). The next time the data directory is opened deletes it.
    /// </exception>
    /// <exception cref="ObjectDisposedException">The client was disposed.</exception>
    /// <exception cref="InvalidOperationException">Another logout is running.</exception>
    public async Task LogoutAsync()
    {
        Task logout;
        lock (_closeLock)
        {
            ObjectDisposedException.ThrowIf(_closing is not null, this);
            if (_logout is not null)
            {
                throw new InvalidOperationException("The client is logging out already.");
            }
            _logout = logout = LogoutCoreAsync();
        }
        await logout.ConfigureAwait(false);
    }

    /// <summary>
    /// Closes the stores of the client (<see cref="Client.Pause"/>), disposes it and releases the data directory. The
    /// session stays stored. Waits for a running <see cref="LogoutAsync"/>.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        Task closing;
        while (true)
        {
            Task? logout;
            lock (_closeLock)
            {
                logout = _logout;
                if (logout is null)
                {
                    closing = _closing ??= CloseAsync(loggedOut: false);
                    break;
                }
            }
            // a successful logout closes the client, a failed one leaves it to this method
            await logout.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
        await closing.ConfigureAwait(false);
    }

    /// <inheritdoc cref="DisposeAsync"/>
    /// <remarks>Blocks until the SDK closed the stores, prefer <see cref="DisposeAsync"/>.</remarks>
    public void Dispose()
    {
        // on the thread pool, so it doesn't wait for a SynchronizationContext it blocks
#pragma warning disable VSTHRD002 // synchronous disposal for callers that can't dispose asynchronously
        Task.Run(() => DisposeAsync().AsTask(), CancellationToken.None).GetAwaiter().GetResult();
#pragma warning restore VSTHRD002
    }

    private static void Validate(ClientStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrWhiteSpace(options.DataDirectory))
        {
            throw new ArgumentException("The data directory is required.", nameof(options));
        }
    }

    private static void Validate(PasswordCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        if (string.IsNullOrWhiteSpace(credentials.Homeserver) || string.IsNullOrWhiteSpace(credentials.Username))
        {
            throw new ArgumentException("The homeserver and the username are required.", nameof(credentials));
        }
    }

    /// <summary>
    /// Runs <paramref name="open"/> on the thread pool (it works with files) without cancellation and waits for it until
    /// <paramref name="cancellationToken"/> is cancelled. An abandoned attempt disposes its client when it finished,
    /// which releases the data directory.
    /// </summary>
    private static async Task<StoredClient?> WaitAsync(
        Func<Task<StoredClient?>> open,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        Task<StoredClient?> opening = Task.Run(open, CancellationToken.None);
        try
        {
            return await opening.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _ = DisposeAbandonedAsync(opening);
            throw;
        }
    }

    private static async Task DisposeAbandonedAsync(Task<StoredClient?> opening)
    {
        StoredClient? client;
        try
        {
#pragma warning disable VSTHRD003 // started by WaitAsync, which doesn't wait for it anymore
            client = await opening.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }
#pragma warning disable S2221, RCS1075 // nobody waits for the result anymore
        catch (Exception)
        {
            // a failed attempt closed its client already
            return;
        }
#pragma warning restore S2221, RCS1075
        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task<StoredClient?> OpenAsync(
        ClientStoreOptions options,
        PasswordCredentials? credentials,
        OpenMode mode
    )
    {
        DataDirectory directory = DataDirectory.Lock(options.DataDirectory);
        try
        {
            SessionFile? session = directory.ReadSession();
            if (session is { State: SessionState.LoggedOut })
            {
                // the device is gone, its keys are useless. Also finishes a logout that crashed while deleting.
                directory.DeleteStore();
                directory.DeleteSession();
                session = null;
            }

            if (session is { State: SessionState.Active, Session: { } stored })
            {
                if (mode == OpenMode.Login)
                {
                    throw new InvalidOperationException(
                        $"{directory.Path} contains a session of {stored.UserId}: restore it or log out first."
                    );
                }
                if (credentials is not null)
                {
                    EnsureSameAccount(directory, stored, credentials);
                }
                if (!directory.HasStore)
                {
                    // a new store would create new keys for a device that published others
                    throw new InvalidOperationException(
                        $"{directory.Path} contains a session but its store is missing. Restore the store from a "
                            + "backup, or delete the directory to log in with a new device."
                    );
                }
                return await RestoreCoreAsync(directory, options, session, stored).ConfigureAwait(false);
            }

            if (mode == OpenMode.Restore)
            {
                directory.Dispose();
                return null;
            }
            return await LoginCoreAsync(directory, options, credentials!, session).ConfigureAwait(false);
        }
        catch
        {
            directory.Dispose();
            throw;
        }
    }

    private static async Task<StoredClient> RestoreCoreAsync(
        DataDirectory directory,
        ClientStoreOptions options,
        SessionFile session,
        StoredSession stored
    )
    {
        // no requests: the stored homeserver URL instead of a discovery, RestoreSession sets the sliding sync version
        // of the session
        OpenedClient opened = await BuildAsync(
                directory,
                options,
                defaults: builder => builder,
                required: builder =>
                    builder
                        .HomeserverUrl(stored.HomeserverUrl)
                        .SlidingSyncVersionBuilder(SlidingSyncVersionBuilder.None)
            )
            .ConfigureAwait(false);
        try
        {
            // the restore starts requests, the session has to be tracked before
            opened.Tracker.Track(session);
            await opened.Client.RestoreSession(stored.ToSession()).ConfigureAwait(false);
        }
        catch
        {
            await opened.CloseAsync().ConfigureAwait(false);
            throw;
        }
        return new StoredClient(directory, opened, session, isRestored: true);
    }

    private static async Task<StoredClient> LoginCoreAsync(
        DataDirectory directory,
        ClientStoreOptions options,
        PasswordCredentials credentials,
        SessionFile? session
    )
    {
        if (string.IsNullOrEmpty(credentials.Password))
        {
            throw new ArgumentException(
                $"{directory.Path} contains no session to restore, logging in needs a password.",
                nameof(credentials)
            );
        }

        (SessionFile pending, string homeserver) = PrepareLogin(directory, credentials, session);
        if (pending != session)
        {
            directory.WriteSession(pending);
        }

        OpenedClient opened = await BuildAsync(
                directory,
                options,
                // the SyncService needs simplified sliding sync, the session stores the discovered version
                defaults: builder => builder.SlidingSyncVersionBuilder(SlidingSyncVersionBuilder.DiscoverNative),
                required: builder => builder.ServerNameOrHomeserverUrl(homeserver)
            )
            .ConfigureAwait(false);
        SessionFile active;
        try
        {
            if (pending.State == SessionState.Pending && !pending.DeviceMayExist)
            {
                // from here on the homeserver may create the device
                directory.WriteSession(pending with { DeviceMayExist = true });
            }
            try
            {
                await opened
                    .Client.Login(credentials.Username, credentials.Password, credentials.DeviceName, pending.DeviceId)
                    .ConfigureAwait(false);
            }
            catch (ClientException.MatrixApi e) when (pending.State == SessionState.Pending && IsRejection(e.@kind))
            {
                // this attempt created no device: if no earlier one did, the next attempt may use another account
                TryWriteSession(directory, pending);
                throw;
            }
            Session loggedIn = opened.Client.Session();
            active = new SessionFile
            {
                State = SessionState.Active,
                DeviceId = loggedIn.DeviceId,
                Session = StoredSession.From(loggedIn),
            };
            directory.WriteSession(active);
            opened.Tracker.Track(active);
        }
        catch
        {
            await opened.CloseAsync().ConfigureAwait(false);
            throw;
        }
        return new StoredClient(directory, opened, active, isRestored: false);
    }

    /// <summary>
    /// The pending login to store before logging in and the homeserver to log in to.
    /// </summary>
    private static (SessionFile Pending, string Homeserver) PrepareLogin(
        DataDirectory directory,
        PasswordCredentials credentials,
        SessionFile? session
    )
    {
        string homeserver = credentials.Homeserver.Trim();
        string username = credentials.Username.Trim();
        switch (session)
        {
            case null when directory.HasStore:
                throw new InvalidOperationException(
                    $"{directory.Path} contains a store but no session.json. The store belongs to one device: restore "
                        + "session.json from a backup, or delete the directory to log in with a new device."
                );
            case null:
                return (NewPendingLogin(homeserver, username), homeserver);
            case { State: SessionState.Pending }
                when string.Equals(session.Username, username, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(session.Homeserver, homeserver, StringComparison.OrdinalIgnoreCase):
                // an earlier attempt may have created the device, reuse it
                return (session, homeserver);
            case { State: SessionState.Pending, DeviceMayExist: false }:
                // no attempt reached the homeserver, the store contains no keys
                directory.DeleteStore();
                return (NewPendingLogin(homeserver, username), homeserver);
            case { State: SessionState.Pending }:
                throw new InvalidOperationException(
                    $"{directory.Path} contains a login of {session.Username} on {session.Homeserver} that may have "
                        + "reached the homeserver: log in with that account, or delete the directory to start over."
                );
            case { State: SessionState.SoftLoggedOut, Session: { } stored }:
                // the device and its keys still exist, logging in with its id continues with the same store
                EnsureSameAccount(directory, stored, credentials);
                return (session, stored.HomeserverUrl);
            default:
                throw new InvalidOperationException($"{directory.Path} contains an invalid session.json.");
        }
    }

    private static SessionFile NewPendingLogin(string homeserver, string username) =>
        new()
        {
            State = SessionState.Pending,
            Homeserver = homeserver,
            Username = username,
            // like Synapse generates them: letters only, so they can't clash with key ids or need escaping in URLs
            DeviceId = RandomNumberGenerator.GetString(DeviceIdCharacters, DeviceIdLength),
        };

    private static void EnsureSameAccount(
        DataDirectory directory,
        StoredSession session,
        PasswordCredentials credentials
    )
    {
        string username = credentials.Username.Trim();
        bool same = username.StartsWith('@')
            ? string.Equals(username, session.UserId, StringComparison.OrdinalIgnoreCase)
            : session.UserId.StartsWith($"@{username}:", StringComparison.OrdinalIgnoreCase);
        if (!same)
        {
            throw new InvalidOperationException(
                $"{directory.Path} belongs to {session.UserId}, not to {credentials.Username}. Use a data directory "
                    + "per account."
            );
        }
    }

    /// <summary>
    /// Errors of the homeserver that reject a login before it creates a device. Not rate limits: the SDK retries the
    /// login, an earlier try may have created the device.
    /// </summary>
    private static bool IsRejection(ErrorKind kind) =>
        kind
            is ErrorKind.Forbidden
                or ErrorKind.InvalidUsername
                or ErrorKind.UserDeactivated
                or ErrorKind.InvalidParam
                or ErrorKind.MissingParam;

    /// <summary>
    /// Writes <paramref name="session"/> while another exception is on its way: if it fails, the old state is the
    /// cautious one.
    /// </summary>
    private static void TryWriteSession(DataDirectory directory, SessionFile session)
    {
        try
        {
            directory.WriteSession(session);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // the device counts as possibly created, a login with another account has to delete the directory
        }
    }

    private static async Task<OpenedClient> BuildAsync(
        DataDirectory directory,
        ClientStoreOptions options,
        Func<ClientBuilder, ClientBuilder> defaults,
        Func<ClientBuilder, ClientBuilder> required
    )
    {
        ClientBuilder builder = defaults(new ClientBuilder());
        if (options.ConfigureClient is { } configure)
        {
            builder = configure(builder);
        }
        using SqliteStoreBuilder store = new SqliteStoreBuilder(directory.StorePath, directory.CachePath).Passphrase(
            options.StorePassphrase
        );
        // a single process: the lock of the data directory keeps the others away
        builder = required(builder)
            .SqliteStore(store)
            .CrossProcessLockConfig(new CrossProcessLockConfig.SingleProcess());

        Client client = await builder.Build().ConfigureAwait(false);
        SessionTracker tracker = new(directory, options.ClientDelegate);
        try
        {
            // before the login or restore, so no authentication error is missed
            return new OpenedClient(client, tracker, client.SetDelegate(tracker));
        }
        catch
        {
            await new OpenedClient(client, tracker, DelegateHandle: null).CloseAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task LogoutCoreAsync()
    {
        try
        {
            try
            {
                await Client.Logout().ConfigureAwait(false);
            }
            catch (ClientException.MatrixApi e) when (e.@kind is ErrorKind.UnknownToken)
            {
                // the homeserver ended the session already
            }
        }
        catch
        {
            lock (_closeLock)
            {
                _logout = null;
            }
            throw;
        }

        Task closing;
        lock (_closeLock)
        {
            // DisposeAsync waits for _logout, so _closing is still free
            _closing = closing = CloseAsync(loggedOut: true);
            _logout = null;
        }
        await closing.ConfigureAwait(false);
    }

    private async Task CloseAsync(bool loggedOut)
    {
        try
        {
            await _opened.CloseAsync().ConfigureAwait(false);
            if (loggedOut)
            {
                // a crash before the deletes finished deletes the rest when the directory is opened again
                _directory.WriteSession(_session with { State = SessionState.LoggedOut });
                _directory.DeleteStore();
                _directory.DeleteSession();
            }
        }
        finally
        {
            _directory.Dispose();
        }
    }

    /// <summary>
    /// A client built for the data directory, with the delegate that tracks its session.
    /// </summary>
    private sealed record OpenedClient(Client Client, SessionTracker Tracker, TaskHandle? DelegateHandle)
    {
        /// <summary>
        /// Closes the stores, so the SDK stops using them before the lock is released (as far as it can, it doesn't
        /// wait for reads forever), and disposes the client.
        /// </summary>
        public async Task CloseAsync()
        {
            Tracker.Dispose();
            DelegateHandle?.Dispose();
            try
            {
                await Client.Pause().ConfigureAwait(false);
            }
            catch (ClientException)
            {
                // the stores close when the last reference to the client is gone
            }
            Client.Dispose();
        }
    }
}

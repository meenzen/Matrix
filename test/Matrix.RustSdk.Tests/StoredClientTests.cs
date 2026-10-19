using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Bindings.Common;
using Matrix.RustSdk.Testing;

namespace Matrix.RustSdk.Tests;

/// <summary>
/// <see cref="StoredClient"/> against a real homeserver, each test with a data directory of its own.
/// </summary>
[Category(Homeserver.Category)]
[ClassDataSource<Homeserver>(Shared = SharedType.PerTestSession)]
public sealed class StoredClientTests(Homeserver homeserver) : IDisposable
{
    private readonly TemporaryDirectory _directory = new();

    private ClientStoreOptions Store => new() { DataDirectory = _directory.Path };

    public void Dispose() => _directory.Dispose();

    [Test]
    public async Task LoginOrRestore_ShouldLogIn_ThenRestoreTheSameDevice()
    {
        // Arrange
        TestUser user = await homeserver.CreateUserAsync("stored");

        // Act
        string deviceId;
        await using (StoredClient first = await StoredClient.LoginOrRestoreAsync(Store, Credentials(user)))
        {
            await Assert.That(first.IsRestored).IsFalse();
            await Assert.That(first.Client.UserId()).IsEqualTo(user.UserId);
            deviceId = first.Client.DeviceId();
        }
        // no password needed for a restore
        await using StoredClient second = await StoredClient.LoginOrRestoreAsync(
            Store,
            Credentials(user, password: null)
        );

        // Assert
        await Assert.That(second.IsRestored).IsTrue();
        await Assert.That(second.Client.DeviceId()).IsEqualTo(deviceId);
        // the restored token works
        await second.Client.SetDisplayName("restored");
        if (!OperatingSystem.IsWindows())
        {
            UnixFileMode sessionMode = File.GetUnixFileMode(Path.Join(_directory.Path, "session.json"));
            await Assert.That(sessionMode).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Test]
    public async Task NewDataDirectory_ShouldOnlyBeReadableByTheOwner()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        // Arrange
        TestUser user = await homeserver.CreateUserAsync("mode");
        string path = Path.Join(_directory.Path, "account");

        // Act
        await using StoredClient _ = await StoredClient.LoginAsync(
            new ClientStoreOptions { DataDirectory = path },
            Credentials(user)
        );

        // Assert
        await Assert
            .That(File.GetUnixFileMode(path))
            .IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    [Test]
    public async Task LoginOrRestore_ShouldThrow_WhenAnotherClientUsesTheDirectory()
    {
        // Arrange
        TestUser user = await homeserver.CreateUserAsync("locked");
        await using StoredClient first = await StoredClient.LoginOrRestoreAsync(Store, Credentials(user));

        // Act & Assert
        await Assert.ThrowsAsync<DataDirectoryLockedException>(() =>
            StoredClient.LoginOrRestoreAsync(Store, Credentials(user))
        );
        await Assert.ThrowsAsync<DataDirectoryLockedException>(() => StoredClient.TryRestoreAsync(Store));
    }

    [Test]
    public async Task TryRestore_ShouldReturnNull_WithoutSession()
    {
        // Act
        StoredClient? client = await StoredClient.TryRestoreAsync(Store);

        // Assert: the directory was released again
        await Assert.That(client).IsNull();
        await Assert.That(await StoredClient.TryRestoreAsync(Store)).IsNull();
    }

    [Test]
    public async Task Login_ShouldThrow_WhenASessionIsStored()
    {
        // Arrange
        TestUser user = await homeserver.CreateUserAsync("twice");
        await (await StoredClient.LoginAsync(Store, Credentials(user))).DisposeAsync();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoredClient.LoginAsync(Store, Credentials(user)));
    }

    [Test]
    public async Task LoginOrRestore_ShouldThrow_WhenTheSessionBelongsToAnotherAccount()
    {
        // Arrange
        TestUser user = await homeserver.CreateUserAsync("owner");
        TestUser other = await homeserver.CreateUserAsync("other");
        await (await StoredClient.LoginAsync(Store, Credentials(user))).DisposeAsync();

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StoredClient.LoginOrRestoreAsync(Store, Credentials(other))
        );
        // the full user id matches too
        PasswordCredentials fullUserId = Credentials(user);
        fullUserId.Username = user.UserId;
        await using StoredClient restored = await StoredClient.LoginOrRestoreAsync(Store, fullUserId);
        await Assert.That(restored.IsRestored).IsTrue();
    }

    [Test]
    public async Task Login_ShouldAcceptAnotherAccount_AfterTheHomeserverRejectedTheLogin()
    {
        // Arrange
        TestUser user = await homeserver.CreateUserAsync("typo");
        PasswordCredentials wrong = Credentials(user);
        wrong.Username = "nobody-" + user.Username;

        // Act
        ClientException.MatrixApi? rejected = await Assert.ThrowsAsync<ClientException.MatrixApi>(() =>
            StoredClient.LoginAsync(Store, wrong)
        );
        await using StoredClient client = await StoredClient.LoginAsync(Store, Credentials(user));

        // Assert
        await Assert.That(rejected?.@kind).IsTypeOf<ErrorKind.Forbidden>();
        await Assert.That(client.Client.UserId()).IsEqualTo(user.UserId);
    }

    [Test]
    public async Task Login_ShouldAcceptAnotherHomeserver_WhenTheFirstOneWasNotFound()
    {
        // Arrange
        TestUser user = await homeserver.CreateUserAsync("unreachable");
        PasswordCredentials typo = Credentials(user);
        typo.Homeserver = "http://localhost:9";

        // Act: the build fails before a login request was sent
        await Assert.ThrowsAsync<ClientBuildException>(() => StoredClient.LoginAsync(Store, typo));
        await using StoredClient client = await StoredClient.LoginAsync(Store, Credentials(user));

        // Assert
        await Assert.That(client.Client.UserId()).IsEqualTo(user.UserId);
    }

    [Test]
    public async Task Login_ShouldKeepTheDevice_WhenARetryOfALoginThatMayHaveCreatedItIsRejected()
    {
        // Arrange: a login that may have created the device, then a wrong password
        TestUser user = await homeserver.CreateUserAsync("retry");
        TestUser other = await homeserver.CreateUserAsync("switch");
        using (DataDirectory directory = DataDirectory.Lock(_directory.Path))
        {
            directory.WriteSession(
                new SessionFile
                {
                    State = SessionState.Pending,
                    Homeserver = homeserver.Url,
                    Username = user.Username,
                    DeviceId = "MAYBEEXIST",
                    DeviceMayExist = true,
                }
            );
        }
        PasswordCredentials wrong = Credentials(user);
        wrong.Password = "wrong";
        await Assert.ThrowsAsync<ClientException.MatrixApi>(() => StoredClient.LoginAsync(Store, wrong));

        // Act & Assert: the store may belong to the device, another account must not take it over
        await Assert.ThrowsAsync<InvalidOperationException>(() => StoredClient.LoginAsync(Store, Credentials(other)));
    }

    [Test]
    public async Task Login_ShouldReuseTheDevice_OfALoginThatDidNotFinish()
    {
        // Arrange: a login that crashed after it stored the device id
        TestUser user = await homeserver.CreateUserAsync("crashed");
        using (DataDirectory directory = DataDirectory.Lock(_directory.Path))
        {
            directory.WriteSession(
                new SessionFile
                {
                    State = SessionState.Pending,
                    Homeserver = homeserver.Url,
                    Username = user.Username,
                    DeviceId = "CRASHEDDEV",
                    DeviceMayExist = true,
                }
            );
        }

        // Act
        await using StoredClient client = await StoredClient.LoginOrRestoreAsync(Store, Credentials(user));

        // Assert
        await Assert.That(client.IsRestored).IsFalse();
        await Assert.That(client.Client.DeviceId()).IsEqualTo("CRASHEDDEV");
    }

    [Test]
    public async Task Login_ShouldContinueWithTheSameDeviceAndStore_AfterASoftLogout()
    {
        // Arrange: tuwunel has no soft logout, the tracker would store this state
        TestUser user = await homeserver.CreateUserAsync("soft");
        string deviceId;
        await using (StoredClient first = await StoredClient.LoginAsync(Store, Credentials(user)))
        {
            deviceId = first.Client.DeviceId();
        }
        using (DataDirectory directory = DataDirectory.Lock(_directory.Path))
        {
            directory.WriteSession(directory.ReadSession()! with { State = SessionState.SoftLoggedOut });
        }

        // Act
        StoredClient? restored = await StoredClient.TryRestoreAsync(Store);
        // the crypto store of the device would throw MismatchedAccount for another device
        await using StoredClient client = await StoredClient.LoginOrRestoreAsync(Store, Credentials(user));

        // Assert
        await Assert.That(restored).IsNull();
        await Assert.That(client.IsRestored).IsFalse();
        await Assert.That(client.Client.DeviceId()).IsEqualTo(deviceId);
    }

    [Test]
    public async Task Login_ShouldThrow_WhenTheStoreHasNoSession()
    {
        // Arrange
        TestUser user = await homeserver.CreateUserAsync("lost");
        await (await StoredClient.LoginAsync(Store, Credentials(user))).DisposeAsync();
        File.Delete(Path.Join(_directory.Path, "session.json"));

        // Act & Assert: the store belongs to a device, a new login must not use it
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            StoredClient.LoginOrRestoreAsync(Store, Credentials(user))
        );
        await Assert.That(Directory.Exists(Path.Join(_directory.Path, "store"))).IsTrue();
    }

    [Test]
    public async Task Logout_ShouldDeleteTheSessionAndTheStore()
    {
        // Arrange
        TestUser user = await homeserver.CreateUserAsync("logout");
        StoredClient client = await StoredClient.LoginAsync(Store, Credentials(user));
        string accessToken = client.Client.Session().AccessToken;

        // Act
        await client.LogoutAsync();

        // Assert
        await Assert.That(await WhoamiAsync(accessToken)).IsEqualTo(HttpStatusCode.Unauthorized);
        await Assert.That(File.Exists(Path.Join(_directory.Path, "session.json"))).IsFalse();
        await Assert.That(Directory.Exists(Path.Join(_directory.Path, "store"))).IsFalse();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => client.LogoutAsync());
        await client.DisposeAsync();
        await Assert.That(await StoredClient.TryRestoreAsync(Store)).IsNull();
        await using StoredClient again = await StoredClient.LoginAsync(Store, Credentials(user));
        await Assert.That(again.Client.UserId()).IsEqualTo(user.UserId);
    }

    [Test]
    public async Task Client_ShouldStartOver_AfterTheHomeserverDeletedTheDevice()
    {
        // Arrange
        TestUser user = await homeserver.CreateUserAsync("revoked");
        AuthErrorRecorder recorder = new();
        ClientStoreOptions store = Store;
        store.ClientDelegate = recorder;
        string deviceId;
        await using (StoredClient client = await StoredClient.LoginAsync(store, Credentials(user)))
        {
            deviceId = client.Client.DeviceId();

            // Act: a logout by another client deletes the device, the next request notices it
            using HttpResponseMessage _ = await PostAsync("/_matrix/client/v3/logout", client.Client.Session());
            await Assert.ThrowsAsync<ClientException>(() => client.Client.SetDisplayName("revoked"));
            await Poll.UntilAsync(() => Task.FromResult(!recorder.Errors.IsEmpty), "the authentication error");
            await Poll.UntilAsync(
                () => Task.FromResult(client.SessionEnded.IsCancellationRequested),
                "SessionEnded to be cancelled"
            );
        }

        // Assert: the session isn't restored and the store of the deleted device is gone
        // not a soft logout, the SDK may report it for several requests
        await Assert.That(recorder.Errors.All(isSoftLogout => !isSoftLogout)).IsTrue();
        await Assert.That(await StoredClient.TryRestoreAsync(Store)).IsNull();
        await Assert.That(Directory.Exists(Path.Join(_directory.Path, "store"))).IsFalse();
        await using StoredClient again = await StoredClient.LoginOrRestoreAsync(Store, Credentials(user));
        await Assert.That(again.IsRestored).IsFalse();
        await Assert.That(again.Client.DeviceId()).IsNotEqualTo(deviceId);
    }

    [Test]
    public async Task Restore_ShouldUseThePassphrase()
    {
        // Arrange
        TestUser user = await homeserver.CreateUserAsync("passphrase");
        ClientStoreOptions encrypted = Store;
        encrypted.StorePassphrase = "secret";
        await (await StoredClient.LoginAsync(encrypted, Credentials(user))).DisposeAsync();

        // Act & Assert
        ClientStoreOptions wrong = Store;
        wrong.StorePassphrase = "wrong";
        await Assert.ThrowsAsync<ClientBuildException>(() => StoredClient.TryRestoreAsync(wrong));
        await using StoredClient? restored = await StoredClient.TryRestoreAsync(encrypted);
        await Assert.That(restored).IsNotNull();
    }

    [Test]
    public async Task LoginOrRestore_ShouldReleaseTheDirectory_WhenTheAbandonedLoginFinished()
    {
        // Arrange
        TestUser user = await homeserver.CreateUserAsync("abandoned");
        using CancellationTokenSource cancellation = new();

        // Act: the login continues in the background
        Task<StoredClient> login = StoredClient.LoginOrRestoreAsync(Store, Credentials(user), cancellation.Token);
        await cancellation.CancelAsync();

        // Assert: the abandoned login stored its session and released the directory
        OperationCanceledException? cancelled = null;
        try
        {
            await login;
        }
        catch (OperationCanceledException e)
        {
            cancelled = e;
        }
        await Assert.That(cancelled).IsNotNull();
        StoredClient restored = await Poll.UntilAsync(
            async () =>
            {
                try
                {
                    return await StoredClient.TryRestoreAsync(Store);
                }
                catch (DataDirectoryLockedException)
                {
                    return null;
                }
            },
            "the abandoned login to finish"
        );
        await using (restored)
        {
            await Assert.That(restored.Client.UserId()).IsEqualTo(user.UserId);
        }
    }

    [Test]
    public async Task Methods_ShouldValidateTheirArguments()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => StoredClient.TryRestoreAsync(new ClientStoreOptions()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            StoredClient.LoginAsync(Store, new PasswordCredentials { Homeserver = "localhost" })
        );
        PasswordCredentials withoutPassword = new() { Homeserver = homeserver.Url, Username = "alice" };
        await Assert.ThrowsAsync<ArgumentException>(() => StoredClient.LoginAsync(Store, withoutPassword));
        // only needed when there is no session
        await Assert.ThrowsAsync<ArgumentException>(() => StoredClient.LoginOrRestoreAsync(Store, withoutPassword));
    }

    private PasswordCredentials Credentials(TestUser user) => Credentials(user, user.Password);

    private PasswordCredentials Credentials(TestUser user, string? password) =>
        new()
        {
            Homeserver = homeserver.Url,
            Username = user.Username,
            Password = password,
        };

    private async Task<HttpStatusCode> WhoamiAsync(string accessToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "/_matrix/client/v3/account/whoami");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using HttpResponseMessage response = await homeserver.Http.SendAsync(request);
        return response.StatusCode;
    }

    private async Task<HttpResponseMessage> PostAsync(string path, Session session)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, path) { Content = new StringContent("{}") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        HttpResponseMessage response = await homeserver.Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private sealed class AuthErrorRecorder : ClientDelegate
    {
        public ConcurrentQueue<bool> Errors { get; } = new();

        public void DidReceiveAuthError(bool isSoftLogout) => Errors.Enqueue(isSoftLogout);

        public void OnBackgroundTaskErrorReport(string taskName, BackgroundTaskFailureReason error) { }
    }
}

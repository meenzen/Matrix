using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Matrix.RustSdk.Bindings;
using TUnit.Core.Interfaces;

namespace Matrix.RustSdk.Testing;

/// <summary>
/// A tuwunel homeserver started with Testcontainers, requires docker with linux containers. Share one instance per
/// test session with <c>[ClassDataSource&lt;Homeserver&gt;(Shared = SharedType.PerTestSession)]</c> and create a
/// separate user per test with <see cref="CreateUserAsync"/>, tests run in parallel.
/// </summary>
public sealed class Homeserver : IAsyncInitializer, IAsyncDisposable
{
    /// <summary>
    /// The category of tests that need a homeserver, platforms without linux containers filter them out.
    /// </summary>
    public const string Category = "Homeserver";

    /// <summary>
    /// The server name, user ids look like <c>@user:localhost</c>.
    /// </summary>
    public const string ServerName = "localhost";

    private const int Port = 8008;

    private readonly ConcurrentBag<Client> _clients = [];

    // created on start: building the container already connects to docker, and TUnit creates the data sources of
    // tests the filter excludes as well, which would break the runs without docker that skip the homeserver tests
    private IContainer? _container;
    private HttpClient? _http;

    /// <summary>
    /// The URL of the client-server API reachable from the host. Plain http, the container only listens locally.
    /// </summary>
    public string Url => $"http://{Container.Hostname}:{Container.GetMappedPublicPort(Port)}"; // NOSONAR

    /// <summary>
    /// A HTTP client for the client-server API, for requests the tests make without the SDK.
    /// </summary>
    public HttpClient Http => _http ?? throw NotStarted();

    private IContainer Container => _container ?? throw NotStarted();

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder("ghcr.io/matrix-construct/tuwunel:v1.9.3")
            .WithEnvironment("TUWUNEL_SERVER_NAME", ServerName)
            .WithEnvironment("TUWUNEL_ADDRESS", "0.0.0.0")
            .WithEnvironment("TUWUNEL_PORT", Port.ToString(CultureInfo.InvariantCulture))
            .WithEnvironment("TUWUNEL_ALLOW_REGISTRATION", "true")
            .WithEnvironment(
                "TUWUNEL_YES_I_AM_VERY_VERY_SURE_I_WANT_AN_OPEN_REGISTRATION_SERVER_PRONE_TO_ABUSE",
                "true"
            )
            .WithEnvironment("TUWUNEL_ALLOW_CHECK_FOR_UPDATES", "false")
            .WithPortBinding(Port, true)
            .WithWaitStrategy(
                Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded(r => r.ForPort(Port).ForPath("/_matrix/client/versions"))
            )
            .WithOutputConsumer(Consume.RedirectStdoutAndStderrToConsole())
            .Build();
        await _container.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(Url) };
        // tuwunel makes the first user its admin and joins it to the admin room, so no test user gets that role
        await CreateUserAsync("admin");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (Client client in _clients)
        {
            client.Dispose();
        }
        _http?.Dispose();
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    private static InvalidOperationException NotStarted() => new("The homeserver hasn't been started.");

    /// <summary>
    /// Registers a new user with a unique name starting with <paramref name="prefix"/>.
    /// </summary>
    public async Task<TestUser> CreateUserAsync(string prefix = "user")
    {
        string username = $"{prefix}-{Guid.NewGuid().ToString("N")[..12]}".ToLowerInvariant();
        string password = Guid.NewGuid().ToString("N");

        using HttpResponseMessage response = await Http.PostAsJsonAsync(
            "/_matrix/client/v3/register",
            new
            {
                username,
                password,
                auth = new { type = "m.login.dummy" },
            }
        );
        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Registering {username} failed with {response.StatusCode}: {body}");
        }

        return new TestUser(username, password, $"@{username}:{ServerName}");
    }

    /// <summary>
    /// Builds a client with an in-memory store and logs <paramref name="user"/> in. The homeserver owns the client and
    /// disposes it together with the container, so tests that depend on each other can share it. The client uses
    /// simplified sliding sync, so it can sync with <see cref="Client.SyncService"/>.
    /// </summary>
    public async Task<Client> LoginAsync(TestUser user)
    {
        Client client = await new ClientBuilder()
            .HomeserverUrl(Url)
            .SlidingSyncVersionBuilder(SlidingSyncVersionBuilder.Native)
            .InMemoryStore()
            .Build();
        _clients.Add(client);
        await client.Login(user.Username, user.Password, initialDeviceName: null, deviceId: null);
        return client;
    }
}

/// <summary>
/// A user registered on the <see cref="Homeserver"/>.
/// </summary>
public sealed record TestUser(string Username, string Password, string UserId);

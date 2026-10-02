using System.Globalization;
using System.Net.Http.Headers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk.Tests;

/// <summary>
/// Integration tests against a tuwunel homeserver, they require docker with linux containers.
/// </summary>
[Category(Category)]
public class SdkTests
{
    public const string Category = "Homeserver";

    private const int HomeserverPort = 8008;

    private readonly IContainer _container = new ContainerBuilder("ghcr.io/matrix-construct/tuwunel:v1.9.3")
        .WithEnvironment("TUWUNEL_SERVER_NAME", "localhost")
        .WithEnvironment("TUWUNEL_ADDRESS", "0.0.0.0")
        .WithEnvironment("TUWUNEL_PORT", HomeserverPort.ToString(CultureInfo.InvariantCulture))
        .WithEnvironment("TUWUNEL_ALLOW_REGISTRATION", "true")
        .WithEnvironment("TUWUNEL_YES_I_AM_VERY_VERY_SURE_I_WANT_AN_OPEN_REGISTRATION_SERVER_PRONE_TO_ABUSE", "true")
        .WithEnvironment("TUWUNEL_ALLOW_CHECK_FOR_UPDATES", "false")
        .WithPortBinding(HomeserverPort, true)
        .WithWaitStrategy(
            Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(HomeserverPort).ForPath("/_matrix/client/versions"))
        )
        .WithOutputConsumer(Consume.RedirectStdoutAndStderrToConsole())
        .Build();

    [Before(Test)]
    public Task StartHomeserver()
    {
        return _container.StartAsync();
    }

    [After(Test)]
    public async Task StopHomeserver()
    {
        await _container.DisposeAsync();
    }

    private string GetHomeserverUrl() =>
        $"http://{_container.Hostname}:{_container.GetMappedPublicPort(HomeserverPort)}";

    [Test]
    public async Task Homeserver_ShouldBeWorking()
    {
        // Arrange
        HttpClient client = new();
        client.BaseAddress = new Uri(GetHomeserverUrl());
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Create user
        var result = await client.PostAsync(
            "/_matrix/client/v3/register",
            new StringContent(
                """
                {
                    "username": "test",
                    "password": "test",
                    "auth": {
                        "type":"m.login.dummy"
                    }
                }
                """
            )
        );

        await Assert.That(result.IsSuccessStatusCode).IsTrue();

        // Login
        result = await client.PostAsync(
            "/_matrix/client/v3/login",
            new StringContent(
                """
                {
                    "type": "m.login.password",
                    "identifier": {
                        "type": "m.id.user",
                        "user": "test"
                    },
                    "password": "test"
                }
                """
            )
        );

        string response = await result.Content.ReadAsStringAsync();
        Console.WriteLine("Login response: " + response);

        await Assert.That(result.IsSuccessStatusCode).IsTrue();
    }

    [Test]
    public async Task Login_ShouldBeSuccessful()
    {
        // Arrange
        HttpClient http = new() { BaseAddress = new Uri(GetHomeserverUrl()) };
        var register = await http.PostAsync(
            "/_matrix/client/v3/register",
            new StringContent(
                """
                {
                    "username": "sdk",
                    "password": "sdk",
                    "auth": {
                        "type": "m.login.dummy"
                    }
                }
                """
            )
        );
        await Assert.That(register.IsSuccessStatusCode).IsTrue();

        using Client client = await new ClientBuilder().HomeserverUrl(GetHomeserverUrl()).InMemoryStore().Build();

        // Act
        await client.Login("sdk", "sdk", null, null);

        CreateRoomParameters parameters = new(
            Name: "TestRoom",
            IsEncrypted: false,
            Visibility: new RoomVisibility.Private(),
            Preset: RoomPreset.PrivateChat
        );
        string roomId = await client.CreateRoom(parameters);

        // Assert
        await Assert.That(client.UserId()).IsEqualTo("@sdk:localhost");
        Room room = await Assert.That(client.Rooms()).HasSingleItem();
        await Assert.That(room.Id()).IsEqualTo(roomId);
    }
}

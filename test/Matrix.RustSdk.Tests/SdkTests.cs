using System.Diagnostics.CodeAnalysis;
using System.Net.Http.Headers;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Matrix.RustSdk.Bindings;
using Xunit.Abstractions;

namespace Matrix.RustSdk.Tests;

public class SdkTests : IAsyncLifetime
{
    private readonly ITestOutputHelper _output;

    private readonly IContainer _container = new ContainerBuilder()
        .WithImage("matrixconduit/matrix-conduit:v0.6.0")
        .WithEnvironment("CONDUIT_SERVER_NAME", "localhost")
        .WithEnvironment("CONDUIT_DATABASE_BACKEND", "rocksdb")
        .WithEnvironment("CONDUIT_ALLOW_REGISTRATION", "true")
        .WithEnvironment("CONDUIT_ALLOW_CHECK_FOR_UPDATES", "false")
        .WithEnvironment("CONDUIT_LOG", "debug")
        .WithExposedPort(6167)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilPortIsAvailable(6167))
        .WithOutputConsumer(Consume.RedirectStdoutAndStderrToConsole())
        .Build();

    public SdkTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public Task InitializeAsync()
    {
        return _container.StartAsync();
    }

    public Task DisposeAsync()
    {
        return _container.DisposeAsync().AsTask();
    }

    private string GetConduitUrl() => $"http://{_container.IpAddress}:6167";

    [Fact]
    public async Task Conduit_ShouldBeWorking()
    {
        // Arrange
        HttpClient client = new();
        client.BaseAddress = new Uri(GetConduitUrl());
        client.DefaultRequestHeaders.Accept.Clear();
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Create user
        var result = await client.PostAsync(
            "/_matrix/client/r0/register",
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

        result.IsSuccessStatusCode.Should().BeTrue();

        // Login
        result = await client.PostAsync(
            "/_matrix/client/r0/login",
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
        _output.WriteLine("Login response: " + response);

        result.IsSuccessStatusCode.Should().BeTrue();
    }

    [Fact]
    public void Login_ShouldBeSuccessful()
    {
        ClientBuilder builder = new();
        Client client = builder.HomeserverUrl(GetConduitUrl()).Username("@admin:localhost").Build();
        client.Login("@admin:localhost", "admin", null, null);

        CreateRoomParameters parameters = new(
            name: "TestRoom",
            isEncrypted: false,
            visibility: RoomVisibility.Private,
            preset: RoomPreset.PrivateChat,
            topic: null,
            avatar: null,
            isDirect: false,
            invite: null
        );
        client.CreateRoom(parameters);

        List<Room> rooms = client.Rooms();
        Assert.Single(rooms);
    }
}

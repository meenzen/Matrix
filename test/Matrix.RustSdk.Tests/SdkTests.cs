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
    public async Task Login_ShouldBeSuccessful()
    {
        // Arrange
        HttpClient http = new() { BaseAddress = new Uri(GetConduitUrl()) };
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
        register.IsSuccessStatusCode.Should().BeTrue();

        using Client client = await new ClientBuilder().HomeserverUrl(GetConduitUrl()).InMemoryStore().Build();

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
        client.UserId().Should().Be("@sdk:localhost");
        client.Rooms().Should().ContainSingle().Which.Id().Should().Be(roomId);
    }
}

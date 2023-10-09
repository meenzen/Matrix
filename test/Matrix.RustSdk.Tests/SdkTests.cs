using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk.Tests;

public class SdkTests : IAsyncLifetime
{
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

    public Task InitializeAsync()
    {
        return _container.StartAsync();
    }

    public Task DisposeAsync()
    {
        return _container.DisposeAsync().AsTask();
    }

    private string GetConduitUrl() => $"http://localhost:{_container.GetMappedPublicPort(6167)}";

    [Fact]
    public void Login_ShouldBeSuccessful()
    {
        ClientBuilder builder = new();
        Client client = builder.HomeserverUrl(GetConduitUrl()).Username("@admin:localhost").Build();
        client.Login("@admin:localhost", "admin", null, null);
        client.StartSync(null);
    }
}

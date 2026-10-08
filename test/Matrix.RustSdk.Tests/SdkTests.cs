using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Testing;

namespace Matrix.RustSdk.Tests;

/// <summary>
/// Integration tests against a tuwunel homeserver, they require docker with linux containers. The tests build on each
/// other with <see cref="DependsOnAttribute"/>: the client logged in by <see cref="Login_ShouldBeSuccessful"/> is
/// reused by the later tests instead of setting everything up again.
/// </summary>
[Category(Homeserver.Category)]
[ClassDataSource<Homeserver>(Shared = SharedType.PerTestSession)]
public class SdkTests(Homeserver homeserver)
{
    // owned and disposed by the homeserver
    private static Client? _client;

    private static Client LoggedInClient =>
        _client ?? throw new InvalidOperationException($"{nameof(Login_ShouldBeSuccessful)} didn't run.");

    [Test]
    public async Task Homeserver_ShouldAllowRegistrationAndLogin()
    {
        // Arrange
        TestUser user = await homeserver.CreateUserAsync("test");

        // Act
        using HttpResponseMessage result = await homeserver.Http.PostAsJsonAsync(
            "/_matrix/client/v3/login",
            new
            {
                type = "m.login.password",
                identifier = new { type = "m.id.user", user = user.Username },
                password = user.Password,
            }
        );

        // Assert
        await Assert.That(result.IsSuccessStatusCode).IsTrue();
    }

    [Test]
    [DependsOn(nameof(Homeserver_ShouldAllowRegistrationAndLogin))]
    public async Task Login_ShouldBeSuccessful()
    {
        // Arrange
        TestUser user = await homeserver.CreateUserAsync("sdk");

        // Act
        _client = await homeserver.LoginAsync(user);

        // Assert
        await Assert.That(_client.UserId()).IsEqualTo(user.UserId);
    }

    [Test]
    [DependsOn(nameof(Login_ShouldBeSuccessful))]
    public async Task CreateRoom_ShouldBeSuccessful()
    {
        // Arrange
        CreateRoomParameters parameters = new(
            Name: "TestRoom",
            IsEncrypted: false,
            Visibility: new RoomVisibility.Private(),
            Preset: RoomPreset.PrivateChat
        );

        // Act
        string roomId = await LoggedInClient.CreateRoom(parameters);

        // Assert
        Room room = await Assert.That(LoggedInClient.Rooms()).HasSingleItem();
        await Assert.That(room.Id()).IsEqualTo(roomId);
    }

    /// <summary>
    /// Exercises an async callback interface: the widget driver awaits
    /// <see cref="WidgetCapabilitiesProvider.AcquireCapabilities"/> during the capability negotiation of the widget
    /// API, the messages a widget would exchange with the driver are sent by the test.
    /// </summary>
    [Test]
    [DependsOn(nameof(CreateRoom_ShouldBeSuccessful))]
    public async Task WidgetDriver_ShouldAwaitAsyncCapabilitiesProvider()
    {
        // Arrange
        const string widgetId = "test-widget";
        const string readCapability = "org.matrix.msc2762.receive.event:m.room.message";
        const string sendCapability = "org.matrix.msc2762.send.event:m.room.message";
        Room room = LoggedInClient.Rooms().Single();
        DelayedCapabilitiesProvider provider = new();
        using WidgetDriverAndHandle widget = MatrixSdkFfiMethods.MakeWidgetDriver(
            new WidgetSettings(widgetId, InitAfterContentLoad: false, RawUrl: "https://widget.example.org")
        );
        Task run = widget.Driver.Run(room, provider);

        // Act
        JsonNode request = await ReceiveAsync(widget.Handle);
        await Assert.That(request["action"]?.GetValue<string>()).IsEqualTo("capabilities");
        Respond(widget.Handle, widgetId, request, new { capabilities = new[] { readCapability, sendCapability } });
        JsonNode notification = await ReceiveAsync(widget.Handle);

        // Assert
        WidgetEventFilter[] roomMessages = [new WidgetEventFilter.MessageLikeWithType("m.room.message")];
        await Assert.That(provider.Requested).IsNotNull();
        await Assert.That(provider.Requested!.Read).IsEquivalentTo(roomMessages);
        await Assert.That(provider.Requested.Send).IsEquivalentTo(roomMessages);
        await Assert.That(notification["action"]?.GetValue<string>()).IsEqualTo("notify_capabilities");
        string[] approved = [.. notification["data"]!["approved"]!.AsArray().Select(c => c!.GetValue<string>())];
        await Assert.That(approved).IsEquivalentTo([readCapability]);

        // The driver keeps running until it fails to send a message to a widget that is gone, so it isn't awaited.
        await Assert.That(run.IsCompleted).IsFalse();
    }

    private static async Task<JsonNode> ReceiveAsync(WidgetDriverHandle handle)
    {
        string? message = await handle.Recv().WaitAsync(TimeSpan.FromSeconds(20));
        return JsonNode.Parse(message ?? throw new InvalidOperationException("The widget driver stopped."))!;
    }

    private static void Respond(WidgetDriverHandle handle, string widgetId, JsonNode request, object response)
    {
        string message = JsonSerializer.Serialize(
            new
            {
                api = "toWidget",
                widgetId,
                requestId = request["requestId"]?.GetValue<string>(),
                action = request["action"]?.GetValue<string>(),
                data = request["data"],
                response,
            }
        );
        if (!handle.Send(message))
        {
            throw new InvalidOperationException("The widget driver stopped.");
        }
    }

    /// <summary>
    /// Completes asynchronously on another thread and only approves the read capabilities, so the test can tell that
    /// the result of the awaited task made it back to the SDK.
    /// </summary>
    private sealed class DelayedCapabilitiesProvider : WidgetCapabilitiesProvider
    {
        public WidgetCapabilities? Requested { get; private set; }

        public async Task<WidgetCapabilities> AcquireCapabilities(WidgetCapabilities capabilities)
        {
            Requested = capabilities;
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            return capabilities with { Send = [] };
        }
    }
}

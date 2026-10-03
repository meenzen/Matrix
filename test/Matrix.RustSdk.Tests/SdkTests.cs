using System.Net.Http.Json;
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
}

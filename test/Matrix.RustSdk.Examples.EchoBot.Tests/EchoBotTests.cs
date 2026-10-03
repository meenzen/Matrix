using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Matrix.RustSdk.Examples.EchoBot.Tests;

/// <summary>
/// End-to-end tests of the echo bot against a tuwunel homeserver. They start the host of the example in-process, the
/// same way <c>Program.cs</c> does, and talk to the bot as a second user. The tests build on each other with
/// <see cref="DependsOnAttribute"/>: the bot started by the first test is reused until the last one stops it.
/// </summary>
[Category(Homeserver.Category)]
[ClassDataSource<Homeserver>(Shared = SharedType.PerTestSession)]
public class EchoBotTests(Homeserver homeserver)
{
    private const string FirstMessage = "Hello, bot!";
    private const string SecondMessage = "Still there?";

    private static IHost? _bot;
    private static TestUser? _botUser;
    private static string? _humanAccessToken;
    private static string? _roomId;

    private static IHost Bot => _bot ?? throw new InvalidOperationException("The bot hasn't been started.");
    private static TestUser BotUser => _botUser ?? throw new InvalidOperationException("The bot hasn't been started.");
    private static string RoomId => _roomId ?? throw new InvalidOperationException("The room hasn't been created.");

    [After(Class)]
    public static async Task StopBotAsync()
    {
        if (_bot is not null)
        {
            await _bot.StopAsync();
            _bot.Dispose();
            _bot = null;
        }
    }

    [Test]
    public async Task Bot_ShouldJoinRoom_WhenInvited()
    {
        // Arrange
        _botUser = await homeserver.CreateUserAsync("echo-bot");
        TestUser human = await homeserver.CreateUserAsync("human");
        Client humanClient = await homeserver.LoginAsync(human);
        _humanAccessToken = humanClient.Session().AccessToken;

        _bot = EchoBotHost.Create([
            $"--EchoBot:Homeserver={homeserver.Url}",
            $"--EchoBot:Username={_botUser.Username}",
            $"--EchoBot:Password={_botUser.Password}",
        ]);
        await _bot.StartAsync();

        // Act
        _roomId = await humanClient.CreateRoom(
            new CreateRoomParameters(
                Name: "Echo",
                IsEncrypted: false,
                Visibility: new RoomVisibility.Private(),
                Preset: RoomPreset.PrivateChat,
                Invite: [_botUser.UserId]
            )
        );

        // Assert
        await Poll.UntilAsync(
            async () => (await GetJoinedMembersAsync()).Contains(BotUser.UserId),
            "the bot to join the room"
        );
    }

    [Test]
    [DependsOn(nameof(Bot_ShouldJoinRoom_WhenInvited))]
    public async Task Bot_ShouldEchoTextMessages()
    {
        // Act
        await SendTextAsync(FirstMessage);

        // Assert
        await Poll.UntilAsync(
            async () => (await GetBotMessagesAsync()).Contains(FirstMessage),
            "the bot to echo the message"
        );
    }

    [Test]
    [DependsOn(nameof(Bot_ShouldEchoTextMessages))]
    public async Task Bot_ShouldNotEchoItsOwnMessages()
    {
        // Act: the bot handles the events of a room in order, if it answered its own echo of the first message, that
        // answer would arrive before the echo of the second message
        await SendTextAsync(SecondMessage);
        await Poll.UntilAsync(
            async () => (await GetBotMessagesAsync()).Contains(SecondMessage),
            "the bot to echo the second message"
        );

        // Assert
        await Assert.That(await GetBotMessagesAsync()).IsEquivalentTo([FirstMessage, SecondMessage]);
    }

    [Test]
    [DependsOn(nameof(Bot_ShouldNotEchoItsOwnMessages))]
    public async Task Bot_ShouldStopGracefully()
    {
        // Arrange
        Task execution = Bot.Services.GetServices<IHostedService>().OfType<EchoBotWorker>().Single().ExecuteTask!;

        // Act: the sync long poll must not delay the shutdown
        await Bot.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // Assert
        await Assert.That(execution.IsCompleted).IsTrue();
        await Assert.That(execution.IsFaulted).IsFalse();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using HttpRequestMessage request = new(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _humanAccessToken);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        HttpResponseMessage response = await homeserver.Http.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private async Task SendTextAsync(string body)
    {
        string path = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(RoomId)}/send/m.room.message/{Guid.NewGuid():N}";
        using HttpResponseMessage response = await SendAsync(HttpMethod.Put, path, new { msgtype = "m.text", body });
    }

    private async Task<string[]> GetJoinedMembersAsync()
    {
        ThrowIfBotFailed();
        string path = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(RoomId)}/joined_members";
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, path);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. json.RootElement.GetProperty("joined").EnumerateObject().Select(member => member.Name)];
    }

    /// <summary>
    /// The bodies of the text messages the bot sent to the room, oldest first.
    /// </summary>
    private async Task<string[]> GetBotMessagesAsync()
    {
        ThrowIfBotFailed();
        string path = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(RoomId)}/messages?dir=b&limit=100";
        using HttpResponseMessage response = await SendAsync(HttpMethod.Get, path);
        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return
        [
            .. json
                .RootElement.GetProperty("chunk")
                .EnumerateArray()
                .Where(e => e.GetProperty("type").GetString() == "m.room.message")
                .Where(e => e.GetProperty("sender").GetString() == BotUser.UserId)
                .Select(e => e.GetProperty("content").GetProperty("body").GetString()!)
                .Reverse(),
        ];
    }

    /// <summary>
    /// Fails fast instead of waiting for the timeout when the bot crashed, for example because the login failed.
    /// </summary>
    private static void ThrowIfBotFailed()
    {
        Task? execution = Bot.Services.GetServices<IHostedService>().OfType<EchoBotWorker>().Single().ExecuteTask;
        if (execution is { IsFaulted: true })
        {
            throw new InvalidOperationException("The bot stopped with an exception.", execution.Exception);
        }
    }
}

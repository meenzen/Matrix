using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Testing;
using TUnit.Assertions.Enums;

namespace Matrix.RustSdk.Tests;

/// <summary>
/// The message and timeline helpers against a real homeserver. The SDK client is the bot, the other users send their
/// events over HTTP.
/// </summary>
[Category(Homeserver.Category)]
[ClassDataSource<Homeserver>(Shared = SharedType.PerTestSession)]
public class MessageTests(Homeserver homeserver)
{
    // updates the lists on thread pool threads, the tests don't depend on the context of the test runner
    private static readonly SynchronizationContext ThreadPool = new();

    [Test]
    public async Task IncomingMessages_ShouldYieldNewMessagesOfOthersOnce()
    {
        // Arrange
        await using BotRoom room = await BotRoom.CreateAsync(homeserver);
        // a second user with the same display name, so the name of Bob is ambiguous
        await (await room.CreateUserAsync("carol", "Bob")).JoinAsync();
        // synced before the timeline is created, the bot also knows about the join of Carol then
        await room.Bob.SendAsync(TextContent("history"));
        await room.WaitForTextAsync("history");
        using Timeline timeline = await room.Room.Timeline();
        await timeline.FetchMembers();
        using CancellationTokenSource timeout = new(Poll.DefaultTimeout);
        List<Received> received = [];

        // Act
        Task receiving = ReceiveAsync();
        await timeline.SendTextAsync("own message");
        string first = await room.Bob.SendAsync(TextContent("first"));
        await room.Bob.SendAsync(
            "m.reaction",
            new Dictionary<string, object>
            {
                ["m.relates_to"] = new
                {
                    rel_type = "m.annotation",
                    event_id = first,
                    key = "👍",
                },
            }
        );
        string notice = await room.Bob.SendAsync(new { msgtype = "m.notice", body = "notice" });
        string second = await room.Bob.SendAsync(TextContent("second", threadRoot: first));
        await receiving;

        // Assert
        await Assert
            .That(received.Select(message => message.Body).ToArray())
            .IsEquivalentTo(new string?[] { "first", "notice", "second" }, CollectionOrdering.Matching);
        await Assert
            .That(received.Select(message => message.EventId).ToArray())
            .IsEquivalentTo(new string?[] { first, notice, second }, CollectionOrdering.Matching);
        await Assert
            .That(received.Select(message => message.Text).ToArray())
            .IsEquivalentTo(new string?[] { "first", null, "second" }, CollectionOrdering.Matching);
        await Assert
            .That(received.Select(message => message.ThreadRoot).ToArray())
            .IsEquivalentTo(new string?[] { null, null, first }, CollectionOrdering.Matching);
        await Assert.That(received[0].Sender).IsEqualTo($"Bob ({room.Bob.User.UserId})");
        await Assert.That(received[0].SentAt).IsBetween(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow);
        await Assert.That(received[0].SentAt.Offset).IsEqualTo(TimeSpan.Zero);
        return;

        async Task ReceiveAsync()
        {
            await foreach (EventTimelineItem message in timeline.WatchIncomingMessagesAsync(timeout.Token))
            {
                using (message)
                {
                    received.Add(
                        new Received(
                            message.EventId,
                            message.Message?.Body,
                            message.TryGetText(out string? text) ? text : null,
                            message.ThreadRootEventId,
                            message.SenderDisplayName,
                            message.SentAt
                        )
                    );
                    if (text == "second")
                    {
                        break;
                    }
                }
            }
        }
    }

    [Test]
    public async Task IncomingMessages_ShouldSkipTheHistoryBeforeTheOwnJoin()
    {
        // Arrange: a timeline of the invited room, the cache of the room is empty
        await using BotRoom room = await BotRoom.CreateAsync(homeserver, invited: true);
        await room.Bob.SendAsync(TextContent("before the join"));
        using Timeline timeline = await room.Room.Timeline();
        using CancellationTokenSource timeout = new(Poll.DefaultTimeout);
        List<string?> received = [];

        // Act: the sync of the join usually contains the latest events before it, with the origin sync. If it does,
        // they arrive before the message after the join
        Task receiving = ReceiveAsync();
        await room.Room.Join();
        await room.Bob.SendAsync(TextContent("after the join"));
        await receiving;

        // Assert
        await Assert.That(received.ToArray()).IsEquivalentTo(new string?[] { "after the join" });
        return;

        async Task ReceiveAsync()
        {
            await foreach (EventTimelineItem message in timeline.WatchIncomingMessagesAsync(timeout.Token))
            {
                using (message)
                {
                    received.Add(message.Message?.Body);
                    if (message.Message?.Body == "after the join")
                    {
                        break;
                    }
                }
            }
        }
    }

    [Test]
    public async Task SendTextAsync_ShouldReplyInTheThread()
    {
        // Arrange
        await using BotRoom room = await BotRoom.CreateAsync(homeserver);
        using Timeline timeline = await room.Room.Timeline();
        using CancellationTokenSource timeout = new(Poll.DefaultTimeout);
        string root = await room.Bob.SendAsync(TextContent("root"));
        string child = await room.Bob.SendAsync(TextContent("child", threadRoot: root));
        await foreach (EventTimelineItem message in timeline.WatchIncomingMessagesAsync(timeout.Token))
        {
            using (message)
            {
                if (message.EventId == child)
                {
                    // the live timeline shows the reply fallback for clients without threads
                    await Assert.That(message.InReplyToEventId).IsEqualTo(root);
                    break;
                }
            }
        }

        // Act
        await timeline.SendTextAsync("reply", inReplyTo: child);

        // Assert
        JsonElement reply = await room.Bob.WaitForEventAsync(room.BotUserId, "reply");
        JsonElement relation = reply.GetProperty("content").GetProperty("m.relates_to");
        await Assert.That(relation.GetProperty("rel_type").GetString()).IsEqualTo("m.thread");
        await Assert.That(relation.GetProperty("event_id").GetString()).IsEqualTo(root);
        await Assert.That(relation.GetProperty("m.in_reply_to").GetProperty("event_id").GetString()).IsEqualTo(child);
    }

    [Test]
    public async Task SendHelpers_ShouldSendTheMessageTypes()
    {
        // Arrange
        await using BotRoom room = await BotRoom.CreateAsync(homeserver);
        using Timeline timeline = await room.Room.Timeline();
        using RoomMessageEventContentWithoutRelation formatted = RoomMessageEventContentWithoutRelation.Notice(
            "formatted notice",
            "<b>formatted</b> notice"
        );

        // Act
        await timeline.SendTextAsync("text");
        await timeline.SendNoticeAsync("notice");
        await timeline.SendMarkdownAsync("**markdown**");
        using SendHandle _ = await timeline.Send(formatted);

        // Assert
        JsonElement text = (await room.Bob.WaitForEventAsync(room.BotUserId, "text")).GetProperty("content");
        await Assert.That(text.GetProperty("msgtype").GetString()).IsEqualTo("m.text");
        await Assert.That(text.TryGetProperty("formatted_body", out JsonElement _)).IsFalse();

        JsonElement notice = (await room.Bob.WaitForEventAsync(room.BotUserId, "notice")).GetProperty("content");
        await Assert.That(notice.GetProperty("msgtype").GetString()).IsEqualTo("m.notice");

        JsonElement markdown = (await room.Bob.WaitForEventAsync(room.BotUserId, "**markdown**")).GetProperty(
            "content"
        );
        await Assert.That(markdown.GetProperty("msgtype").GetString()).IsEqualTo("m.text");
        await Assert.That(markdown.GetProperty("formatted_body").GetString()).Contains("<strong>markdown</strong>");

        JsonElement html = (await room.Bob.WaitForEventAsync(room.BotUserId, "formatted notice")).GetProperty(
            "content"
        );
        await Assert.That(html.GetProperty("msgtype").GetString()).IsEqualTo("m.notice");
        await Assert.That(html.GetProperty("format").GetString()).IsEqualTo("org.matrix.custom.html");
        await Assert.That(html.GetProperty("formatted_body").GetString()).IsEqualTo("<b>formatted</b> notice");
    }

    private static object TextContent(string body, string? threadRoot = null) =>
        threadRoot is null
            ? new { msgtype = "m.text", body }
            : new Dictionary<string, object>
            {
                ["msgtype"] = "m.text",
                ["body"] = body,
                ["m.relates_to"] = new Dictionary<string, object>
                {
                    ["rel_type"] = "m.thread",
                    ["event_id"] = threadRoot,
                    // the reply fallback for clients without threads
                    ["is_falling_back"] = true,
                    ["m.in_reply_to"] = new { event_id = threadRoot },
                },
            };

    private sealed record Received(
        string? EventId,
        string? Body,
        string? Text,
        string? ThreadRoot,
        string Sender,
        DateTimeOffset SentAt
    );

    /// <summary>
    /// A user that talks to the homeserver over HTTP.
    /// </summary>
    private sealed class HttpUser(Homeserver homeserver, TestUser user, string accessToken, string roomId)
    {
        public TestUser User { get; } = user;

        public Task<string> SendAsync(object content) => SendAsync("m.room.message", content);

        public async Task<string> SendAsync(string type, object content)
        {
            string path = $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}/send/{type}/{Guid.NewGuid():N}";
            using HttpResponseMessage response = await RequestAsync(HttpMethod.Put, path, content);
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return json.RootElement.GetProperty("event_id").GetString()!;
        }

        public async Task JoinAsync()
        {
            using HttpResponseMessage _ = await RequestAsync(
                HttpMethod.Post,
                $"/_matrix/client/v3/join/{Uri.EscapeDataString(roomId)}",
                new { }
            );
        }

        public async Task SetDisplayNameAsync(string displayName)
        {
            using HttpResponseMessage _ = await RequestAsync(
                HttpMethod.Put,
                $"/_matrix/client/v3/profile/{Uri.EscapeDataString(User.UserId)}/displayname",
                new { displayname = displayName }
            );
        }

        /// <summary>
        /// Waits for the message of <paramref name="sender"/> with <paramref name="body"/> and returns the event.
        /// </summary>
        public async Task<JsonElement> WaitForEventAsync(string sender, string body) =>
            (JsonElement)
                await Poll.UntilAsync<object>(
                    async () =>
                    {
                        string path =
                            $"/_matrix/client/v3/rooms/{Uri.EscapeDataString(roomId)}/messages?dir=b&limit=100";
                        using HttpResponseMessage response = await RequestAsync(HttpMethod.Get, path);
                        using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                        foreach (JsonElement e in json.RootElement.GetProperty("chunk").EnumerateArray())
                        {
                            if (
                                e.GetProperty("type").GetString() == "m.room.message"
                                && e.GetProperty("sender").GetString() == sender
                                && e.GetProperty("content").TryGetProperty("body", out JsonElement b)
                                && b.GetString() == body
                            )
                            {
                                return e.Clone();
                            }
                        }
                        return null;
                    },
                    $"the message '{body}'"
                );

        private async Task<HttpResponseMessage> RequestAsync(HttpMethod method, string path, object? body = null)
        {
            using HttpRequestMessage request = new(method, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            if (body is not null)
            {
                request.Content = JsonContent.Create(body);
            }

            HttpResponseMessage response = await homeserver.Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                string error = await response.Content.ReadAsStringAsync();
                response.Dispose();
                throw new InvalidOperationException($"{method} {path} failed with {response.StatusCode}: {error}");
            }
            return response;
        }
    }

    /// <summary>
    /// A room of the syncing bot (the SDK client) with Bob, who sends his events over HTTP.
    /// </summary>
    private sealed class BotRoom(Homeserver homeserver, Client client, SyncService syncService, Room room, HttpUser bob)
        : IAsyncDisposable
    {
        public Room Room { get; } = room;

        public HttpUser Bob { get; } = bob;

        public string BotUserId { get; } = client.UserId();

        /// <summary>
        /// Creates the room, or lets Bob create it and invite the bot if <paramref name="invited"/> is set, the bot
        /// doesn't join it then.
        /// </summary>
        public static async Task<BotRoom> CreateAsync(Homeserver homeserver, bool invited = false)
        {
            Client client = await homeserver.LoginAsync(await homeserver.CreateUserAsync("bot"));
            HttpUser bob;
            string roomId;
            if (invited)
            {
                TestUser bobUser = await homeserver.CreateUserAsync("bob");
                // the homeserver owns the client
                Client bobClient = await homeserver.LoginAsync(bobUser);
                roomId = await bobClient.CreateRoom(
                    new CreateRoomParameters(
                        Name: "invited",
                        IsEncrypted: false,
                        Visibility: new RoomVisibility.Private(),
                        Preset: RoomPreset.PrivateChat,
                        Invite: [client.UserId()]
                    )
                );
                bob = new HttpUser(homeserver, bobUser, bobClient.Session().AccessToken, roomId);
            }
            else
            {
                roomId = await client.CreateRoom(
                    new CreateRoomParameters(
                        Name: "messages",
                        IsEncrypted: false,
                        Visibility: new RoomVisibility.Private(),
                        Preset: RoomPreset.PublicChat
                    )
                );
                bob = await CreateUserAsync(homeserver, roomId, "bob", "Bob");
                await bob.JoinAsync();
            }

            // the room list syncs only the latest event of every room by default, several messages between two syncs
            // would leave a gap in the timeline
            using SyncServiceBuilder builder = client.SyncService();
            using SyncServiceBuilder withLimit = builder.WithRoomListTimelineLimit(20);
            SyncService syncService = await withLimit.Finish();
            await syncService.Start();
            Room room = await Poll.UntilAsync(() => Task.FromResult(client.GetRoom(roomId)), "the room to be synced");
            return new BotRoom(homeserver, client, syncService, room, bob);
        }

        public Task<HttpUser> CreateUserAsync(string prefix, string displayName) =>
            CreateUserAsync(homeserver, Room.Id(), prefix, displayName);

        /// <summary>
        /// Waits until the bot's client received a text message with <paramref name="body"/>.
        /// </summary>
        public async Task WaitForTextAsync(string body)
        {
            using Timeline timeline = await Room.Timeline();
            await using LiveList<string?> texts = timeline
                .WatchItemDiffsAsync()
                .ToLiveList(
                    item =>
                    {
                        using EventTimelineItem? eventItem = item.AsEvent();
                        return eventItem is not null && eventItem.TryGetText(out string? text) ? text : null;
                    },
                    synchronizationContext: ThreadPool
                );
            await Poll.UntilAsync(() => Task.FromResult(texts.Contains(body)), $"the text '{body}'");
        }

        public async ValueTask DisposeAsync()
        {
            await syncService.Stop();
            syncService.Dispose();
            Room.Dispose();
        }

        private static async Task<HttpUser> CreateUserAsync(
            Homeserver homeserver,
            string roomId,
            string prefix,
            string displayName
        )
        {
            TestUser user = await homeserver.CreateUserAsync(prefix);
            // the homeserver owns the client
            Client login = await homeserver.LoginAsync(user);
            HttpUser http = new(homeserver, user, login.Session().AccessToken, roomId);
            await http.SetDisplayNameAsync(displayName);
            return http;
        }
    }
}

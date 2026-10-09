using Matrix.RustSdk.Examples.TuiClient.Chat;
using Matrix.RustSdk.Examples.TuiClient.Rendering;
using Matrix.RustSdk.Testing;

namespace Matrix.RustSdk.Examples.TuiClient.Tests.Chat;

public class TimelineRendererTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 10, 18, 0, 0, TimeSpan.Zero);

    private static TimelineEntry Message(
        string key,
        string sender,
        string body,
        int minutesAgo,
        EntryKind kind = EntryKind.Message
    ) =>
        new(key, kind, body)
        {
            SenderId = $"@{sender}:example.org",
            SenderName = sender,
            Time = Now.AddMinutes(-minutesAgo),
        };

    private static string Render(IReadOnlyList<TimelineEntry> entries, int width) =>
        string.Join('\n', TimelineRenderer.Layout(entries, width, Now).Select(r => $"{r.Item}|{r.Text}|"));

    [Test]
    public async Task Layout_ShouldRenderAConversation()
    {
        // Arrange
        TimelineEntry[] entries =
        [
            new("d1", EntryKind.DayDivider, "") { Time = Now.AddDays(-1) },
            Message("1", "alice", "hello everyone, this message is long enough to wrap onto the next line", 1500),
            Message("2", "alice", "a follow-up", 1499),
            Message("3", "bob", "joined", 1400, EntryKind.Event) with
            {
                Body = "bob joined",
            },
            new("d2", EntryKind.DayDivider, "") { Time = Now },
            Message("4", "bob", "waves", 30, EntryKind.Emote),
            Message("5", "bob", "sure", 20) with
            {
                ReplyTo = new ReplyPreview("$1", "alice", "hello everyone, this message is long enough"),
                Reactions = [new ReactionSummary("👍", 2, IsOwn: true), new ReactionSummary("🎉", 1, IsOwn: false)],
                ReadBy = ["@carol:example.org"],
                IsEdited = true,
            },
            new("m", EntryKind.ReadMarker, "new messages"),
            Message("6", "me", "on my way", 1) with
            {
                IsOwn = true,
                Status = SendStatus.Sending,
            },
            Message("7", "carol", "", 0, EntryKind.Media) with
            {
                Media = new MediaAttachment("image", "cat.png", "image/png", 123_456, "{}"),
            },
        ];

        // Act
        string rendered = Render(entries, 60);

        // Assert
        await Snapshot.VerifyAsync(rendered);
    }

    [Test]
    public async Task Layout_ShouldColorMentionsAndOwnMessages()
    {
        // Arrange
        TimelineEntry[] entries =
        [
            Message("1", "alice", "hey me", 2) with
            {
                MentionsMe = true,
            },
            Message("2", "me", "hi", 1) with
            {
                IsOwn = true,
            },
        ];

        // Act
        IReadOnlyList<Row> rows = TimelineRenderer.Layout(entries, 60, Now);

        // Assert
        await Assert.That(rows[0].Spans[0].Role).IsEqualTo(Role.Mention);
        await Assert.That(rows[1].Spans[2].Role).IsEqualTo(Role.OwnSender);
        await Assert
            .That(rows[0].Spans[2])
            .IsEqualTo(new Span(" alice", Role.Sender, Theme.SenderColor("@alice:example.org")));
    }

    [Test]
    [Arguments(0, "Today")]
    [Arguments(1, "Yesterday")]
    [Arguments(3, "Wed, 7 Oct")]
    [Arguments(400, "Fri, 5 Sep 2025")]
    public async Task DayLabel_ShouldBeRelativeToToday(int daysAgo, string expected)
    {
        // Act
        string label = TimelineRenderer.DayLabel(Now.AddDays(-daysAgo), Now);

        // Assert
        await Assert.That(label).IsEqualTo(expected);
    }

    [Test]
    [Arguments(null, null)]
    [Arguments(512ul, "512 B")]
    [Arguments(1536ul, "1.5 KB")]
    [Arguments(5_242_880ul, "5 MB")]
    public async Task FormatSize_ShouldUseBinaryUnits(ulong? size, string? expected)
    {
        // Act
        string? formatted = TimelineRenderer.FormatSize(size);

        // Assert
        await Assert.That(formatted).IsEqualTo(expected);
    }
}

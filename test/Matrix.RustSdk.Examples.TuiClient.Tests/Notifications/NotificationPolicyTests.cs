using Matrix.RustSdk.Examples.TuiClient.Notifications;

namespace Matrix.RustSdk.Examples.TuiClient.Tests.Notifications;

public class NotificationPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 10, 12, 0, 0, TimeSpan.Zero);

    private static IncomingNotification Notification(
        bool isDirect = false,
        bool hasMention = false,
        string roomId = "!room",
        int minutesAfterStart = 1
    ) => new(roomId, "Book club", "Alice", "hello", isDirect, hasMention, Start.AddMinutes(minutesAfterStart));

    [Test]
    public async Task ShouldNotify_ShouldNotifyForMentionsAndDirectMessages()
    {
        // Assert
        await Assert.That(NotificationPolicy.ShouldNotify(Notification(hasMention: true), null, Start)).IsTrue();
        await Assert.That(NotificationPolicy.ShouldNotify(Notification(isDirect: true), null, Start)).IsTrue();
        await Assert.That(NotificationPolicy.ShouldNotify(Notification(), null, Start)).IsFalse();
        await Assert
            .That(NotificationPolicy.ShouldNotify(Notification() with { IsNoisy = true }, null, Start))
            .IsTrue();
    }

    [Test]
    public async Task ShouldNotify_ShouldSkipTheRoomTheUserReads()
    {
        // Act
        bool notify = NotificationPolicy.ShouldNotify(Notification(isDirect: true), "!room", Start);

        // Assert
        await Assert.That(notify).IsFalse();
    }

    [Test]
    public async Task ShouldNotify_ShouldSkipMessagesFromBeforeTheStart()
    {
        // Act: the first sync delivers what was missed while the client wasn't running
        bool notify = NotificationPolicy.ShouldNotify(
            Notification(isDirect: true, minutesAfterStart: -10),
            null,
            Start
        );

        // Assert
        await Assert.That(notify).IsFalse();
    }

    [Test]
    public async Task Format_ShouldNameTheRoomUnlessItIsDirect()
    {
        // Act
        (string Title, string Body) direct = NotificationPolicy.Format(Notification(isDirect: true));
        (string Title, string Body) group = NotificationPolicy.Format(Notification(hasMention: true));

        // Assert
        await Assert.That(direct).IsEqualTo(("Alice", "hello"));
        await Assert.That(group).IsEqualTo(("Alice in Book club", "hello"));
    }
}

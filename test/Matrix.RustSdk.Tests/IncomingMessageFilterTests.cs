using Matrix.RustSdk.Bindings.Ui;
using TUnit.Assertions.Enums;
using Item = Matrix.RustSdk.IncomingMessageFilter.Item;

namespace Matrix.RustSdk.Tests;

/// <summary>
/// Which timeline items count as incoming messages, without the SDK.
/// </summary>
public class IncomingMessageFilterTests
{
    [Test]
    public async Task Filter_ShouldAcceptNewMessagesOfOthersFromSync()
    {
        // Arrange
        IncomingMessageFilter filter = new();

        // Act
        List<int> incoming = filter.Filter([Message("$1"), Message("$2")]);

        // Assert
        await Assert.That(incoming).IsEquivalentTo([0, 1], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(null, false, EventItemOrigin.Sync)]
    [Arguments("$1", true, EventItemOrigin.Sync)]
    [Arguments("$1", false, EventItemOrigin.Cache)]
    [Arguments("$1", false, EventItemOrigin.Pagination)]
    [Arguments("$1", false, EventItemOrigin.Local)]
    [Arguments("$1", false, null)]
    public async Task Filter_ShouldRejectOtherEvents(string? eventId, bool isOwn, EventItemOrigin? origin)
    {
        // Arrange
        IncomingMessageFilter filter = new();

        // Act
        List<int> incoming = filter.Filter([new Item(eventId, isOwn, origin, IsMessage: true)]);

        // Assert
        await Assert.That(incoming).IsEmpty();
    }

    [Test]
    public async Task Filter_ShouldAcceptEachMessageOnce()
    {
        // Arrange
        IncomingMessageFilter filter = new();
        filter.Filter([Message("$1")]);

        // Act: a reaction or a read receipt changes the item
        List<int> incoming = filter.Filter([Message("$1")]);

        // Assert
        await Assert.That(incoming).IsEmpty();
    }

    [Test]
    [Arguments(EventItemOrigin.Pagination)]
    [Arguments(EventItemOrigin.Cache)]
    public async Task Filter_ShouldRejectOldEventsThatAreAddedAgain(EventItemOrigin origin)
    {
        // Arrange
        IncomingMessageFilter filter = new();
        filter.Filter([new Item("$1", IsOwn: false, origin, IsMessage: true)]);

        // Act: a gappy sync clears the timeline and adds the events of the sync again
        List<int> incoming = filter.Filter([Message("$1")]);

        // Assert
        await Assert.That(incoming).IsEmpty();
    }

    [Test]
    public async Task Filter_ShouldAcceptMessagesOnceTheyAreDecrypted()
    {
        // Arrange
        IncomingMessageFilter filter = new();
        List<int> encrypted = filter.Filter([Encrypted("$1")]);

        // Act
        List<int> decrypted = filter.Filter([Message("$1")]);

        // Assert
        await Assert.That(encrypted).IsEmpty();
        await Assert.That(decrypted).IsEquivalentTo([0]);
    }

    [Test]
    public async Task Filter_ShouldRejectTheHistoryBeforeTheOwnJoin()
    {
        // Arrange
        IncomingMessageFilter filter = new();
        Item join = new("$join", IsOwn: true, EventItemOrigin.Sync, IsMessage: false, IsOwnJoin: true);

        // Act: the sync of the join contains the latest events before it
        List<int> incoming = filter.Filter([Message("$1"), Encrypted("$2"), join, Message("$3")]);
        List<int> decrypted = filter.Filter([Message("$2")]);

        // Assert
        await Assert.That(incoming).IsEquivalentTo([3]);
        await Assert.That(decrypted).IsEmpty();
    }

    [Test]
    public async Task Filter_ShouldForgetTheOldestEvents()
    {
        // Arrange
        IncomingMessageFilter filter = new(capacity: 2);
        filter.Filter([Message("$1"), Message("$2"), Message("$3")]);

        // Act
        List<int> oldest = filter.Filter([Message("$1")]);
        List<int> newest = filter.Filter([Message("$3")]);

        // Assert
        await Assert.That(oldest).IsEquivalentTo([0]);
        await Assert.That(newest).IsEmpty();
    }

    private static Item Message(string eventId) => new(eventId, IsOwn: false, EventItemOrigin.Sync, IsMessage: true);

    private static Item Encrypted(string eventId) => new(eventId, IsOwn: false, EventItemOrigin.Sync, IsMessage: false);
}

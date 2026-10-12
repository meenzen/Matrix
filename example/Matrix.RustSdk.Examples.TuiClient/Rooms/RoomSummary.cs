using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Bindings.Base;

namespace Matrix.RustSdk.Examples.TuiClient.Rooms;

/// <summary>
/// A room in the room list. It keeps managed values instead of the <see cref="Room"/>: the room list replaces its rooms
/// with new objects whenever something changes, <see cref="MatrixSession.GetRoom"/> gets the room when it is opened.
/// </summary>
public sealed record RoomSummary(string RoomId, string Name)
{
    public Membership Membership { get; init; } = Membership.Joined;
    public bool IsDirect { get; init; }
    public bool IsEncrypted { get; init; }
    public bool IsFavourite { get; init; }
    public string? Topic { get; init; }

    /// <summary>
    /// The name of the user who sent the invite, for invites.
    /// </summary>
    public string? Inviter { get; init; }

    public ulong UnreadMessages { get; init; }
    public ulong UnreadMentions { get; init; }
    public ulong UnreadNotifications { get; init; }
    public bool IsMarkedUnread { get; init; }
    public ulong JoinedMembers { get; init; }

    public bool IsInvite => Membership == Membership.Invited;

    public bool IsUnread => UnreadMessages > 0 || IsMarkedUnread || IsInvite;

    public override string ToString() => IsInvite ? $"{Name} (invite)" : Name;

    /// <summary>
    /// Copies what the client shows of <paramref name="info"/>.
    /// </summary>
    public static RoomSummary From(RoomInfo info) =>
        new(info.Id, string.IsNullOrWhiteSpace(info.DisplayName) ? info.Id : info.DisplayName)
        {
            Membership = info.Membership,
            IsDirect = info.IsDm || info.IsDirect,
            IsEncrypted = info.EncryptionState == EncryptionState.Encrypted,
            IsFavourite = info.IsFavourite,
            Topic = info.Topic,
            Inviter = info.Inviter is { } inviter ? inviter.DisplayName ?? inviter.UserId : null,
            // the counts of the client miss messages of rooms the sliding sync only sends the latest event of, the counts
            // of the server miss what it can't evaluate in encrypted rooms: the larger one is right
            UnreadMessages = Math.Max(info.NumUnreadMessages, info.NotificationCount),
            UnreadMentions = Math.Max(info.NumUnreadMentions, info.HighlightCount),
            UnreadNotifications = info.NumUnreadNotifications,
            IsMarkedUnread = info.IsMarkedUnread,
            JoinedMembers = info.JoinedMembersCount,
        };
}

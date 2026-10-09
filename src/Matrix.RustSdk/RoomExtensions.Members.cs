namespace Matrix.RustSdk.Bindings;

public static partial class RoomExtensions
{
    /// <summary>
    /// Gets the members of the room, loading the member list from the homeserver if the client doesn't have it yet.
    /// </summary>
    /// <param name="room">The room.</param>
    /// <returns>
    /// The members with any membership: joined, invited, left, banned and knocking users. Filter them with
    /// <c>member.Membership is MembershipState.Join</c>.
    /// </returns>
    /// <remarks>
    /// The sync loads members lazily, only those of the events it delivers. The first call in a room loads the complete
    /// list, later calls read it from the store. In rooms the user is invited to it is only loaded if invited users may
    /// see the history, otherwise the result are the stored members. <see cref="Room.Member"/> gets a single member,
    /// <see cref="Room.JoinedMembersCount"/> counts them.
    /// </remarks>
    /// <exception cref="ClientException">The member list couldn't be loaded.</exception>
    public static async Task<RoomMember[]> GetMembersAsync(this Room room)
    {
        ArgumentNullException.ThrowIfNull(room);
        using RoomMembersIterator members = await room.Members().ConfigureAwait(false);
        return ReadAll(members);
    }

    /// <summary>
    /// Gets the members of the room the client has stored, without a request to the homeserver.
    /// </summary>
    /// <param name="room">The room.</param>
    /// <returns>
    /// The stored members with any membership, see <see cref="GetMembersAsync"/>. Until the member list was loaded
    /// once, only the members the sync delivered.
    /// </returns>
    /// <exception cref="ClientException">The store couldn't be read.</exception>
    public static async Task<RoomMember[]> GetCachedMembersAsync(this Room room)
    {
        ArgumentNullException.ThrowIfNull(room);
        using RoomMembersIterator members = await room.MembersNoSync().ConfigureAwait(false);
        return ReadAll(members);
    }

    private static RoomMember[] ReadAll(RoomMembersIterator members)
    {
        // the iterator holds a snapshot and copies the rest of it on every chunk, one chunk takes everything
        List<RoomMember> all = [];
        while (members.NextChunk(uint.MaxValue) is { } chunk)
        {
            all.AddRange(chunk);
        }
        return [.. all];
    }
}

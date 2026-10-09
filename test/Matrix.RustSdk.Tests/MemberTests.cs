using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Testing;

namespace Matrix.RustSdk.Tests;

/// <summary>
/// The room member helpers against a real homeserver.
/// </summary>
[Category(Homeserver.Category)]
[ClassDataSource<Homeserver>(Shared = SharedType.PerTestSession)]
public class MemberTests(Homeserver homeserver)
{
    [Test]
    public async Task GetMembers_ShouldReturnEveryMembership()
    {
        // Arrange
        Client owner = await homeserver.LoginAsync(await homeserver.CreateUserAsync("owner"));
        string roomId = await owner.CreateRoom(
            new CreateRoomParameters(
                Name: "members",
                IsEncrypted: false,
                Visibility: new RoomVisibility.Private(),
                Preset: RoomPreset.PublicChat
            )
        );
        using Room room = owner.GetRoom(roomId)!;
        Client joined = await homeserver.LoginAsync(await homeserver.CreateUserAsync("joined"));
        using Room _ = await joined.JoinRoomById(roomId);
        Client left = await homeserver.LoginAsync(await homeserver.CreateUserAsync("left"));
        using (Room leftRoom = await left.JoinRoomById(roomId))
        {
            await leftRoom.Leave();
        }
        TestUser invited = await homeserver.CreateUserAsync("invited");
        await room.InviteUserById(invited.UserId);

        // Act
        RoomMember[] members = await room.GetMembersAsync();
        RoomMember[] cached = await room.GetCachedMembersAsync();

        // Assert
        Dictionary<string, MembershipState> memberships = members.ToDictionary(
            member => member.UserId,
            member => member.Membership
        );
        await Assert.That(memberships.Count).IsEqualTo(4);
        await Assert.That(memberships[owner.UserId()]).IsTypeOf<MembershipState.Join>();
        await Assert.That(memberships[joined.UserId()]).IsTypeOf<MembershipState.Join>();
        await Assert.That(memberships[left.UserId()]).IsTypeOf<MembershipState.Leave>();
        await Assert.That(memberships[invited.UserId]).IsTypeOf<MembershipState.Invite>();
        // the member list was loaded, the store has it now
        await Assert
            .That(cached.Select(member => member.UserId).Order())
            .IsEquivalentTo(members.Select(member => member.UserId).Order());
    }
}

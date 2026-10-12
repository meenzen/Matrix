using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk.Examples.TuiClient.Chat;

/// <summary>
/// A member of a room as the member list shows it.
/// </summary>
public sealed record MemberSummary(string UserId, string Name, bool IsInvited, long PowerLevel, bool IsIgnored)
{
    /// <summary>
    /// The role for the power level, like Element shows it.
    /// </summary>
    public string? Role =>
        PowerLevel switch
        {
            >= 150 => "owner",
            >= 100 => "admin",
            >= 50 => "moderator",
            _ => null,
        };

    /// <summary>
    /// The joined and invited members, sorted by power level and then by name. Members who left or were banned are
    /// skipped.
    /// </summary>
    public static IReadOnlyList<MemberSummary> From(IEnumerable<RoomMember> members) =>
        [
            .. members
                .Where(m => m.Membership is MembershipState.Join or MembershipState.Invite)
                .Select(m => new MemberSummary(
                    m.UserId,
                    string.IsNullOrWhiteSpace(m.DisplayName) ? m.UserId : m.DisplayName,
                    m.Membership is MembershipState.Invite,
                    m.PowerLevel switch
                    {
                        // the creators of v12 rooms have an infinite power level
                        Bindings.PowerLevel.Infinite => long.MaxValue,
                        Bindings.PowerLevel.Value value => value.ValueValue,
                        _ => 0,
                    },
                    m.IsIgnored
                ))
                .OrderByDescending(m => m.PowerLevel)
                .ThenBy(m => m.IsInvited)
                .ThenBy(m => m.Name, StringComparer.CurrentCultureIgnoreCase),
        ];
}

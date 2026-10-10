using System.Globalization;
using Matrix.RustSdk.Examples.TuiClient.Rendering;

namespace Matrix.RustSdk.Examples.TuiClient.Rooms;

/// <summary>
/// Lays out the room list: one line per room with a marker for the kind of room, the name and the number of unread
/// messages, mentions are counted separately (<c>@2</c>). Unread rooms are bold, the open room is marked with a bar.
/// </summary>
public static class RoomListRenderer
{
    public static IReadOnlyList<Row> Layout(IReadOnlyList<RoomSummary> rooms, int width, string? openRoomId)
    {
        List<Row> rows = new(rooms.Count);
        for (int i = 0; i < rooms.Count; i++)
        {
            rows.Add(LayoutRoom(i, rooms[i], width, rooms[i].RoomId == openRoomId));
        }
        return rows;
    }

    private static Row LayoutRoom(int item, RoomSummary room, int width, bool isOpen)
    {
        string marker = isOpen ? "▌" : " ";
        string kind = room switch
        {
            { IsInvite: true } => "+",
            { IsDirect: true } => "@",
            _ => "#",
        };

        string badge = Badge(room);
        int nameWidth = Math.Max(1, width - 2 - 1 - (badge.Length > 0 ? badge.Length + 1 : 0));
        string name = TextLayout.Truncate(room.Name, nameWidth);
        int padding = Math.Max(0, width - 2 - 1 - TextLayout.Width(name) - badge.Length);

        Role nameRole = room switch
        {
            { IsInvite: true } => Role.Invite,
            { UnreadMentions: > 0 } => Role.Mention,
            { IsUnread: true } => Role.Unread,
            _ => Role.Normal,
        };
        List<Span> spans =
        [
            new(marker, Role.Highlight),
            new(kind, Role.Dim),
            new(" "),
            new(name, nameRole),
            new(new string(' ', padding)),
        ];
        if (badge.Length > 0)
        {
            spans.Add(new Span(badge, room.UnreadMentions > 0 ? Role.Mention : Role.Unread));
        }
        return new Row(item, spans);
    }

    /// <summary>
    /// <c>3</c> unread messages, <c>@1</c> mention, <c>!</c> for a room marked as unread.
    /// </summary>
    public static string Badge(RoomSummary room)
    {
        if (room.IsInvite)
        {
            return "";
        }
        if (room.UnreadMentions > 0)
        {
            return "@" + room.UnreadMentions.ToString(CultureInfo.InvariantCulture);
        }
        if (room.UnreadMessages > 0)
        {
            return room.UnreadMessages > 99 ? "99+" : room.UnreadMessages.ToString(CultureInfo.InvariantCulture);
        }
        return room.IsMarkedUnread ? "!" : "";
    }
}

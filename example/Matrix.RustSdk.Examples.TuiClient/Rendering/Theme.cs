using Terminal.Gui.Drawing;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Matrix.RustSdk.Examples.TuiClient.Rendering;

/// <summary>
/// The colors of the <see cref="Role"/>s, on the background of the view. Only the 16 ANSI colors are used, terminals
/// map them to their own palette.
/// </summary>
public static class Theme
{
    private static readonly ColorName16[] SenderColors =
    [
        ColorName16.BrightCyan,
        ColorName16.BrightGreen,
        ColorName16.BrightMagenta,
        ColorName16.BrightYellow,
        ColorName16.BrightBlue,
        ColorName16.Cyan,
        ColorName16.Green,
        ColorName16.Magenta,
    ];

    public static int SenderColorCount => SenderColors.Length;

    /// <summary>
    /// A stable color for a user id, so a sender keeps their color across rooms and restarts.
    /// </summary>
    public static int SenderColor(string userId)
    {
        // string.GetHashCode is randomized per process
        uint hash = 2166136261;
        foreach (char c in userId)
        {
            hash = (hash ^ c) * 16777619;
        }
        return (int)(hash % (uint)SenderColors.Length);
    }

    public static Attribute Resolve(Span span, Attribute normal, bool selected)
    {
        Color background = normal.Background;
        Attribute attribute = span.Role switch
        {
            Role.Dim or Role.Time => new Attribute(ColorName16.DarkGray, background),
            Role.Sender => new Attribute(SenderColors[span.Color % SenderColors.Length], background, TextStyle.Bold),
            Role.OwnSender => new Attribute(ColorName16.White, background, TextStyle.Bold),
            Role.Notice => new Attribute(ColorName16.Gray, background, TextStyle.Italic),
            Role.Emote => new Attribute(normal.Foreground, background, TextStyle.Italic),
            Role.Error => new Attribute(ColorName16.BrightRed, background),
            Role.Mention => new Attribute(ColorName16.BrightRed, background, TextStyle.Bold),
            Role.Quote => new Attribute(ColorName16.Gray, background),
            Role.Reaction => new Attribute(ColorName16.Yellow, background),
            Role.Unread => new Attribute(normal.Foreground, background, TextStyle.Bold),
            Role.Highlight => new Attribute(ColorName16.BrightYellow, background, TextStyle.Bold),
            Role.Invite => new Attribute(ColorName16.BrightGreen, background, TextStyle.Italic),
            Role.Link => new Attribute(ColorName16.BrightBlue, background, TextStyle.Underline),
            _ => normal,
        };
        return selected
            ? new Attribute(attribute.Foreground, attribute.Background, attribute.Style | TextStyle.Reverse)
            : attribute;
    }
}

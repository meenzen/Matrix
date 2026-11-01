using System.Globalization;
using System.Text;
using Terminal.Gui.Text;

namespace Matrix.RustSdk.Examples.TuiClient.Rendering;

/// <summary>
/// What a piece of text is, the views map it to colors (<see cref="Theme"/>). Keeping colors out of the layout keeps it
/// testable without a terminal.
/// </summary>
public enum Role
{
    Normal,
    Dim,
    Time,
    Sender,
    OwnSender,
    Notice,
    Emote,
    Error,
    Mention,
    Quote,
    Reaction,
    Unread,
    Highlight,
    Invite,
    Link,
}

/// <summary>
/// A piece of text with one role. <see cref="Color"/> picks one of the sender colors for <see cref="Role.Sender"/>.
/// </summary>
public readonly record struct Span(string Text, Role Role = Role.Normal, int Color = 0);

/// <summary>
/// One line on the screen. <see cref="Item"/> is the index of the item (message, room, member) the line belongs to, an
/// item can take several lines.
/// </summary>
public sealed record Row(int Item, IReadOnlyList<Span> Spans)
{
    public string Text => string.Concat(Spans.Select(s => s.Text));

    public override string ToString() => Text;
}

/// <summary>
/// Measures and wraps text by terminal columns: wide characters (CJK, most emoji) take two columns, combining
/// characters none. Text is split into grapheme clusters so an emoji sequence is never broken apart.
/// </summary>
public static class TextLayout
{
    /// <summary>
    /// The number of columns <paramref name="text"/> takes in the terminal.
    /// </summary>
    public static int Width(string text) => text.GetColumns();

    /// <summary>
    /// Cuts <paramref name="text"/> to at most <paramref name="width"/> columns, ending with an ellipsis if it was cut.
    /// </summary>
    public static string Truncate(string text, int width)
    {
        if (width <= 0)
        {
            return "";
        }
        if (Width(text) <= width)
        {
            return text;
        }
        StringBuilder result = new();
        int used = 0;
        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            int columns = Width(element);
            if (used + columns > width - 1)
            {
                break;
            }
            result.Append(element);
            used += columns;
        }
        return result.Append('…').ToString();
    }

    /// <summary>
    /// Wraps <paramref name="text"/> into lines of at most <paramref name="width"/> columns. Line breaks in the text
    /// are kept, lines break at spaces where possible and within words that are longer than a line.
    /// </summary>
    public static IReadOnlyList<string> Wrap(string text, int width)
    {
        width = Math.Max(width, 1);
        List<string> lines = [];
        foreach (string paragraph in text.ReplaceLineEndings("\n").Split('\n'))
        {
            WrapParagraph(paragraph.Replace('\t', ' '), width, lines);
        }
        return lines;
    }

    private static void WrapParagraph(string paragraph, int width, List<string> lines)
    {
        StringBuilder line = new();
        int lineWidth = 0;
        // the end of the last space in the line, where the line can be broken
        int breakAt = -1;
        int widthAtBreak = 0;

        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(paragraph);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            int columns = Width(element);
            if (element == " " && lineWidth + columns > width)
            {
                // the space ends the line
                lines.Add(line.ToString().TrimEnd());
                line.Clear();
                lineWidth = 0;
                breakAt = -1;
                continue;
            }
            while (lineWidth + columns > width && lineWidth > 0)
            {
                if (breakAt > 0)
                {
                    string rest = line.ToString(breakAt, line.Length - breakAt);
                    lines.Add(line.ToString(0, breakAt).TrimEnd());
                    line.Clear().Append(rest);
                    lineWidth -= widthAtBreak;
                }
                else
                {
                    lines.Add(line.ToString());
                    line.Clear();
                    lineWidth = 0;
                }
                breakAt = -1;
            }
            line.Append(element);
            lineWidth += columns;
            if (element == " ")
            {
                breakAt = line.Length;
                widthAtBreak = lineWidth;
            }
        }
        lines.Add(line.ToString().TrimEnd());
    }
}

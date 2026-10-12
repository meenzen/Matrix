namespace Matrix.RustSdk.Examples.TuiClient.Rendering;

/// <summary>
/// What a page is about, so it can be refreshed when its data changes.
/// </summary>
public enum PageKind
{
    Help,
    Members,
    Invite,
    Verification,
    Encryption,
    Text,
}

/// <summary>
/// A page shown instead of the timeline: help, the member list, an invite, the verification. Pages are lists of
/// lines that can be scrolled like the timeline, <see cref="Open"/> is what Enter does on a line.
/// </summary>
public sealed record Page(PageKind Kind, string Title, IReadOnlyList<PageLine> Lines)
{
    public Func<int, Task>? Open { get; init; }

    /// <summary>
    /// The rows of the page for a width, long lines are wrapped.
    /// </summary>
    public IReadOnlyList<Row> Layout(int width)
    {
        List<Row> rows = [];
        for (int i = 0; i < Lines.Count; i++)
        {
            PageLine line = Lines[i];
            string indent = new(' ', line.Indent);
            int textWidth = Math.Max(10, width - line.Indent - TextLayout.Width(line.Label ?? ""));
            IReadOnlyList<string> wrapped = TextLayout.Wrap(line.Text, textWidth);
            for (int j = 0; j < wrapped.Count; j++)
            {
                List<Span> spans = [new(indent)];
                if (line.Label is { } label)
                {
                    spans.Add(
                        j == 0 ? new Span(label, Role.Highlight) : new Span(new string(' ', TextLayout.Width(label)))
                    );
                }
                spans.Add(new Span(wrapped[j], line.Role));
                rows.Add(new Row(i, spans));
            }
        }
        return rows;
    }
}

/// <summary>
/// A line of a <see cref="Page"/>: an optional label in its own column (a key, a name) and the text.
/// </summary>
public sealed record PageLine(string Text, Role Role = Role.Normal, string? Label = null, int Indent = 1)
{
    public static readonly PageLine Empty = new("");

    public static PageLine Heading(string text) => new(text, Role.Unread);
}

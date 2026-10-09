using System.Globalization;
using Matrix.RustSdk.Examples.TuiClient.Rendering;

namespace Matrix.RustSdk.Examples.TuiClient.Chat;

/// <summary>
/// Lays out timeline entries as rows, like IRC clients do: the time, the sender in a column of its own and the
/// message wrapped next to it. Consecutive messages of a sender only show the name once. Replies, reactions, read
/// receipts and the send state get lines of their own below or above the message.
/// </summary>
public static class TimelineRenderer
{
    private const int TimeWidth = 5;
    private const int MinSenderWidth = 6;
    private const int MaxSenderWidth = 20;
    private static readonly TimeSpan GroupingWindow = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The rows of <paramref name="entries"/> for a view <paramref name="width"/> columns wide.
    /// <paramref name="now"/> decides which days are "Today" and "Yesterday", times are shown in the time zone of
    /// <paramref name="now"/>.
    /// </summary>
    public static IReadOnlyList<Row> Layout(IReadOnlyList<TimelineEntry> entries, int width, DateTimeOffset now)
    {
        int senderWidth = Math.Clamp(
            entries
                .Where(e => e.SenderName is not null && e.IsEvent)
                .Select(e => TextLayout.Width(e.SenderName!))
                .DefaultIfEmpty(0)
                .Max(),
            MinSenderWidth,
            Math.Max(MinSenderWidth, Math.Min(MaxSenderWidth, width / 4))
        );
        int indent = TimeWidth + 1 + senderWidth + 1;
        int bodyWidth = Math.Max(10, width - indent);
        List<Row> rows = [];

        for (int i = 0; i < entries.Count; i++)
        {
            TimelineEntry entry = entries[i];
            TimelineEntry? previous = i > 0 ? entries[i - 1] : null;
            switch (entry.Kind)
            {
                case EntryKind.DayDivider:
                    rows.Add(Divider(i, DayLabel(entry.Time ?? now, now), width, Role.Dim));
                    break;
                case EntryKind.ReadMarker:
                    rows.Add(Divider(i, entry.Body, width, Role.Unread));
                    break;
                default:
                    LayoutEvent(rows, i, entry, previous, now, senderWidth, indent, bodyWidth);
                    break;
            }
        }
        return rows;
    }

    public static string DayLabel(DateTimeOffset day, DateTimeOffset now)
    {
        DateTime date = day.ToOffset(now.Offset).Date;
        if (date == now.Date)
        {
            return "Today";
        }
        if (date == now.Date.AddDays(-1))
        {
            return "Yesterday";
        }
        string format = date.Year == now.Year ? "ddd, d MMM" : "ddd, d MMM yyyy";
        return date.ToString(format, CultureInfo.InvariantCulture);
    }

    private static Row Divider(int item, string label, int width, Role role)
    {
        string text = $" {label} ";
        int side = Math.Max(2, (width - TextLayout.Width(text)) / 2);
        return new Row(item, [new Span(new string('─', side) + text + new string('─', side), role)]);
    }

    private static void LayoutEvent(
        List<Row> rows,
        int item,
        TimelineEntry entry,
        TimelineEntry? previous,
        DateTimeOffset now,
        int senderWidth,
        int indent,
        int bodyWidth
    )
    {
        string time = entry.Time?.ToOffset(now.Offset).ToString("HH:mm", CultureInfo.InvariantCulture) ?? "     ";
        Span timeSpan = new(time, entry.MentionsMe ? Role.Mention : Role.Time);
        string blank = new(' ', indent);

        // the sender column: the name, "*" for emotes and events, nothing for follow-up messages
        Span sender;
        Role bodyRole;
        string body = entry.Body;
        switch (entry.Kind)
        {
            case EntryKind.Event:
                sender = new Span(Pad("*", senderWidth, alignRight: true), Role.Dim);
                bodyRole = Role.Dim;
                break;
            case EntryKind.Emote:
                sender = new Span(Pad("*", senderWidth, alignRight: true), Role.Emote);
                body = $"{entry.SenderName} {entry.Body}";
                bodyRole = Role.Emote;
                break;
            default:
                string name = IsFollowUp(entry, previous) ? "" : entry.SenderName ?? "";
                sender = new Span(
                    Pad(TextLayout.Truncate(name, senderWidth), senderWidth, alignRight: true),
                    entry.IsOwn ? Role.OwnSender : Role.Sender,
                    Theme.SenderColor(entry.SenderId ?? "")
                );
                bodyRole = entry.Kind switch
                {
                    EntryKind.Notice => Role.Notice,
                    EntryKind.Redacted or EntryKind.Encrypted => Role.Dim,
                    _ => Role.Normal,
                };
                break;
        }

        List<List<Span>> lines = [];
        if (entry.ReplyTo is { } reply)
        {
            string quoted = reply.Body is null
                ? "↳ (loading the message)"
                : $"↳ {reply.SenderName ?? "?"}: {reply.Body.ReplaceLineEndings(" ")}";
            lines.Add([new Span(TextLayout.Truncate(quoted, bodyWidth), Role.Quote)]);
        }
        if (entry.Media is { } media)
        {
            string details = string.Join(", ", new[] { media.MimeType, FormatSize(media.Size) }.OfType<string>());
            string label = $"[{media.Kind}] {media.Filename}" + (details.Length > 0 ? $" ({details})" : "");
            foreach (string line in TextLayout.Wrap(label, bodyWidth))
            {
                lines.Add([new Span(line, Role.Link)]);
            }
        }
        if (body.Length > 0 || entry.Media is null)
        {
            foreach (string line in TextLayout.Wrap(body, bodyWidth))
            {
                lines.Add([new Span(line, bodyRole)]);
            }
        }

        // the state of the message at the end of its last line, if it fits
        Span? suffix = entry.Status switch
        {
            SendStatus.Sending => new Span(" (sending…)", Role.Dim),
            SendStatus.Failed => new Span(" (failed to send)", Role.Error),
            _ when entry.IsEdited => new Span(" (edited)", Role.Dim),
            _ => null,
        };
        if (suffix is { } s)
        {
            List<Span> last = lines[^1];
            int used = last.Sum(span => TextLayout.Width(span.Text));
            if (used + TextLayout.Width(s.Text) <= bodyWidth)
            {
                last.Add(s);
            }
            else
            {
                lines.Add([s with { Text = s.Text.TrimStart() }]);
            }
        }

        if (entry.Reactions.Count > 0)
        {
            List<Span> reactions = [];
            foreach (ReactionSummary reaction in entry.Reactions)
            {
                if (reactions.Count > 0)
                {
                    reactions.Add(new Span("  "));
                }
                string text = reaction.Count > 1 ? $"{reaction.Key} {reaction.Count}" : reaction.Key;
                reactions.Add(new Span(text, reaction.IsOwn ? Role.Highlight : Role.Reaction));
            }
            lines.Add(reactions);
        }
        if (entry.ReadBy.Count > 0)
        {
            string readers = "✓ " + string.Join(", ", entry.ReadBy.Select(TimelineEntries.Localpart));
            lines.Add([new Span(TextLayout.Truncate(readers, bodyWidth), Role.Dim)]);
        }

        for (int i = 0; i < lines.Count; i++)
        {
            List<Span> spans = i == 0 ? [timeSpan, new Span(" "), sender, new Span(" ")] : [new Span(blank)];
            spans.AddRange(lines[i]);
            rows.Add(new Row(item, spans));
        }
    }

    /// <summary>
    /// Whether the message continues the previous one of the same sender, so the name isn't repeated.
    /// </summary>
    private static bool IsFollowUp(TimelineEntry entry, TimelineEntry? previous) =>
        previous is { Kind: not (EntryKind.Event or EntryKind.Emote or EntryKind.DayDivider or EntryKind.ReadMarker) }
        && previous.SenderId == entry.SenderId
        && entry.Time is { } time
        && previous.Time is { } previousTime
        && time - previousTime < GroupingWindow;

    private static string Pad(string text, int width, bool alignRight)
    {
        int missing = Math.Max(0, width - TextLayout.Width(text));
        return alignRight ? new string(' ', missing) + text : text + new string(' ', missing);
    }

    public static string? FormatSize(ulong? size) =>
        size switch
        {
            null => null,
            < 1024 => $"{size} B",
            < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{size / 1024.0:0.#} KB"),
            < 1024 * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{size / (1024.0 * 1024):0.#} MB"),
            _ => string.Create(CultureInfo.InvariantCulture, $"{size / (1024.0 * 1024 * 1024):0.#} GB"),
        };
}

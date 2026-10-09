using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Examples.TuiClient.Chat;
using Matrix.RustSdk.Examples.TuiClient.Input;
using Matrix.RustSdk.Examples.TuiClient.Rooms;
using Matrix.RustSdk.Examples.TuiClient.Security;

namespace Matrix.RustSdk.Examples.TuiClient.Rendering;

/// <summary>
/// The contents of the pages shown instead of the timeline.
/// </summary>
public static class Pages
{
    private static readonly (string Keys, string Description)[] Keys =
    [
        ("j k ↓ ↑", "move down / up (a count like 5j moves further)"),
        ("gg G", "first / last"),
        ("C-d C-u", "half a page down / up, C-f C-b a page"),
        ("h l ← →", "focus the room list / the timeline, Tab switches"),
        ("J K C-n C-p", "next / previous room"),
        ("U", "next unread room"),
        ("Enter", "open the room, or what is selected on a page"),
        ("i a", "write a message (insert mode), Esc goes back to normal mode"),
        ("Enter", "in insert mode: send, Alt+Enter adds a line break"),
        ("r", "reply to the selected message"),
        ("e", "edit the selected message"),
        ("dd", "delete the selected message"),
        ("+", "react to the selected message"),
        ("yy", "copy the selected message"),
        ("o", "open the attachment of the selected message"),
        ("m", "members of the room"),
        ("/", "filter the room list by name, Esc clears the filter"),
        (":", "command line, Tab completes, ↑ ↓ history"),
        ("?", "this help, q or Esc closes pages"),
        ("ZZ :q C-q", "quit"),
    ];

    public static Page Help()
    {
        List<PageLine> lines = [PageLine.Heading("Keys"), PageLine.Empty];
        int keyWidth = Keys.Max(k => k.Keys.Length) + 2;
        lines.AddRange(Keys.Select(k => new PageLine(k.Description, Label: k.Keys.PadRight(keyWidth))));
        lines.Add(PageLine.Empty);
        lines.Add(PageLine.Heading("Commands"));
        lines.Add(PageLine.Empty);
        int usageWidth = CommandParser.Help.Max(c => c.Usage.Length) + 3;
        lines.AddRange(
            CommandParser.Help.Select(c => new PageLine(c.Description, Label: (":" + c.Usage).PadRight(usageWidth)))
        );
        return new Page(PageKind.Help, "Help", lines);
    }

    public static Page Members(string roomName, IReadOnlyList<MemberSummary> members)
    {
        List<PageLine> lines = [];
        foreach (MemberSummary member in members)
        {
            string details = string.Join(
                ", ",
                new[]
                {
                    member.Role,
                    member.IsInvited ? "invited" : null,
                    member.IsIgnored ? "ignored" : null,
                }.OfType<string>()
            );
            string text = member.Name == member.UserId ? member.UserId : $"{member.Name}  {member.UserId}";
            lines.Add(
                new PageLine(
                    details.Length > 0 ? $"{text}  ({details})" : text,
                    member.IsInvited ? Role.Dim : Role.Normal
                )
            );
        }
        int joined = members.Count(m => !m.IsInvited);
        return new Page(PageKind.Members, $"Members of {roomName} ({joined})", lines);
    }

    public static Page Invite(RoomSummary room) =>
        new(
            PageKind.Invite,
            room.Name,
            [
                PageLine.Empty,
                new PageLine(
                    room.Inviter is { } inviter
                        ? $"{inviter} invited you to {room.Name}."
                        : $"You are invited to {room.Name}.",
                    Role.Invite
                ),
                PageLine.Empty,
                new PageLine("Accept the invite with y or :accept, decline it with n or :decline."),
            ]
        );

    public static Page Verification(VerificationStatus status)
    {
        string partner = status.Partner ?? "the other side";
        List<PageLine> lines = [PageLine.Empty];
        switch (status.Step)
        {
            case VerificationStep.Requested:
                lines.Add(new PageLine($"Waiting for {partner} to accept the verification request…"));
                lines.Add(new PageLine("Esc cancels.", Role.Dim));
                break;
            case VerificationStep.Incoming:
                lines.Add(new PageLine($"{partner} wants to verify.", Role.Invite));
                lines.Add(PageLine.Empty);
                lines.Add(new PageLine("Accept with y, decline with n."));
                break;
            case VerificationStep.Accepted:
                lines.Add(new PageLine("Waiting for the emojis…"));
                break;
            case VerificationStep.Comparing:
                lines.Add(new PageLine($"Compare these with what {partner} shows:"));
                lines.Add(PageLine.Empty);
                lines.AddRange(
                    status.Emojis.Select(e => new PageLine(e.Description, Label: $"{e.Symbol}  ", Indent: 3))
                );
                if (status.Decimals.Count > 0)
                {
                    lines.Add(new PageLine(string.Join("  ", status.Decimals), Role.Highlight, Indent: 3));
                }
                lines.Add(PageLine.Empty);
                lines.Add(new PageLine("Do they match? y: they match, n: they don't."));
                break;
            case VerificationStep.Confirmed:
                lines.Add(new PageLine($"Waiting for {partner} to confirm…"));
                break;
            case VerificationStep.Done:
                lines.Add(new PageLine($"Verified with {partner}.", Role.Invite));
                break;
            case VerificationStep.Cancelled:
                lines.Add(new PageLine("The verification was cancelled.", Role.Error));
                break;
            case VerificationStep.Failed:
                lines.Add(new PageLine("The verification failed.", Role.Error));
                break;
            default:
                lines.Add(new PageLine("No verification is going on. :verify verifies this session with another one."));
                break;
        }
        return new Page(PageKind.Verification, "Verification", lines);
    }

    public static Page Encryption(EncryptionState state, string userId, string deviceId, string? recoveryKey = null)
    {
        List<PageLine> lines =
        [
            PageLine.Empty,
            new PageLine(userId, Label: "User      "),
            new PageLine(deviceId, Label: "Session   "),
            new PageLine(
                state.Verification switch
                {
                    VerificationState.Verified => "verified",
                    VerificationState.Unverified => "not verified: :verify with another session or :recovery <key>",
                    _ => "unknown",
                },
                state.Verification == VerificationState.Verified ? Role.Normal : Role.Error,
                Label: "Session is "
            ),
            new PageLine(
                state.Backup switch
                {
                    BackupState.Enabled => "enabled, new room keys are backed up",
                    BackupState.Downloading => "downloading keys",
                    BackupState.Creating or BackupState.Enabling or BackupState.Resuming => "being enabled",
                    BackupState.Disabling => "being disabled",
                    _ => "not enabled",
                },
                Label: "Backup    "
            ),
            new PageLine(
                state.Recovery switch
                {
                    RecoveryState.Enabled => "enabled",
                    RecoveryState.Incomplete => "incomplete: enter the recovery key with :recovery <key>",
                    RecoveryState.Disabled => "not set up: :recovery enable creates a recovery key",
                    _ => "unknown",
                },
                state.Recovery == RecoveryState.Incomplete ? Role.Error : Role.Normal,
                Label: "Recovery  "
            ),
        ];
        if (recoveryKey is not null)
        {
            lines.Add(PageLine.Empty);
            lines.Add(new PageLine("Your recovery key, store it somewhere safe:", Role.Unread));
            lines.Add(new PageLine(recoveryKey, Role.Highlight, Indent: 3));
            lines.Add(PageLine.Empty);
            lines.Add(
                new PageLine(
                    "It verifies new sessions and restores the message history. It isn't shown again.",
                    Role.Dim
                )
            );
        }
        return new Page(PageKind.Encryption, "Encryption", lines);
    }

    public static Page Text(string title, string text) =>
        new(PageKind.Text, title, [.. text.ReplaceLineEndings("\n").Split('\n').Select(l => new PageLine(l))]);
}

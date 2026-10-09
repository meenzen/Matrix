using System.Text;

namespace Matrix.RustSdk.Examples.TuiClient.Input;

/// <summary>
/// A <c>:</c> command typed in command mode, see <see cref="CommandParser"/> for the syntax and
/// <see cref="CommandParser.Help"/> for the list.
/// </summary>
public abstract record Command
{
    public sealed record Quit : Command;

    public sealed record Logout : Command;

    public sealed record Help : Command;

    public sealed record Join(string RoomIdOrAlias) : Command;

    public sealed record Leave : Command;

    public sealed record Accept : Command;

    public sealed record Decline : Command;

    public sealed record Invite(string UserId) : Command;

    public sealed record Kick(string UserId, string? Reason) : Command;

    public sealed record Ban(string UserId, string? Reason) : Command;

    public sealed record Unban(string UserId) : Command;

    public sealed record CreateRoom(string Name, bool IsEncrypted, bool IsPublic) : Command;

    public sealed record DirectMessage(string UserId) : Command;

    public sealed record Members : Command;

    public sealed record Topic(string? Text) : Command;

    public sealed record RoomName(string Name) : Command;

    public sealed record Emote(string Text) : Command;

    public sealed record React(string Key) : Command;

    public sealed record Upload(string Path) : Command;

    public sealed record Open : Command;

    public sealed record Save(string? Path) : Command;

    public sealed record MarkRead : Command;

    public sealed record Ignore(string UserId) : Command;

    public sealed record Unignore(string UserId) : Command;

    public sealed record Verify : Command;

    public sealed record VerifyUser(string UserId) : Command;

    public sealed record RecoveryStatus : Command;

    public sealed record EnableRecovery : Command;

    public sealed record Recover(string RecoveryKey) : Command;

    public sealed record ResetRecoveryKey : Command;

    public sealed record Notifications(bool? Enabled) : Command;

    public sealed record Search(string? Query) : Command;
}

/// <summary>
/// The result of <see cref="CommandParser.Parse"/>: the command or an error message for the status line.
/// </summary>
public readonly record struct ParsedCommand(Command? Command, string? Error)
{
    public static implicit operator ParsedCommand(Command command) => new(command, null);

    public static ParsedCommand Fail(string error) => new(null, error);
}

/// <summary>
/// Parses command lines like <c>join #room:example.org</c> (without the <c>:</c>). Arguments are separated by spaces,
/// quotes group an argument with spaces (<c>create "Book club"</c>). Commands that take free text (<c>topic</c>,
/// <c>me</c>, the reason of <c>kick</c>) use the rest of the line as is. Commands can be abbreviated where vim does
/// (<c>q</c>, <c>h</c>).
/// </summary>
public static class CommandParser
{
    /// <summary>
    /// One line per command for the help screen and completion.
    /// </summary>
    public static readonly IReadOnlyList<(string Name, string Usage, string Description)> Help =
    [
        ("quit", "q[uit]", "quit, the session stays stored"),
        ("logout", "logout", "log out and delete the stored session"),
        ("help", "h[elp]", "show the keys and commands"),
        ("join", "join <#alias|!id>", "join a room"),
        ("leave", "leave", "leave the open room"),
        ("accept", "accept", "accept the invite to the open room"),
        ("decline", "decline", "decline the invite to the open room"),
        ("invite", "invite <@user>", "invite a user to the open room"),
        ("kick", "kick <@user> [reason]", "remove a user from the open room"),
        ("ban", "ban <@user> [reason]", "ban a user from the open room"),
        ("unban", "unban <@user>", "lift a ban"),
        ("create", "create <name> [--public] [--unencrypted]", "create a room and open it"),
        ("dm", "dm <@user>", "open the direct chat with a user, creating it if needed"),
        ("members", "members", "show the members of the open room"),
        ("topic", "topic [text]", "show or set the topic of the open room"),
        ("name", "name <name>", "rename the open room"),
        ("me", "me <text>", "send an emote"),
        ("react", "react <emoji>", "react to the selected message"),
        ("upload", "upload <path>", "send a file"),
        ("open", "open", "open the attachment of the selected message"),
        ("save", "save [path]", "save the attachment of the selected message"),
        ("read", "read", "mark the open room as read"),
        ("ignore", "ignore <@user>", "ignore a user"),
        ("unignore", "unignore <@user>", "stop ignoring a user"),
        ("verify", "verify [@user]", "verify this session with another one, or verify a user"),
        ("recovery", "recovery [enable|reset|<key>]", "show the key backup status, enable recovery or recover"),
        ("notifications", "notifications [on|off]", "toggle desktop notifications"),
        ("search", "search [text]", "filter the room list, like /"),
    ];

    public static ParsedCommand Parse(string line)
    {
        string trimmed = line.Trim();
        if (trimmed.StartsWith(':'))
        {
            trimmed = trimmed[1..].TrimStart();
        }
        if (trimmed.Length == 0)
        {
            return ParsedCommand.Fail("No command.");
        }

        int space = trimmed.IndexOf(' ', StringComparison.Ordinal);
        string name = space < 0 ? trimmed : trimmed[..space];
        string rest = space < 0 ? "" : trimmed[(space + 1)..].Trim();
        List<string> args = Split(rest);

        switch (name.ToLowerInvariant())
        {
            case "q":
            case "quit":
            case "q!":
            case "qa":
            case "wq":
                return new Command.Quit();
            case "logout":
                return new Command.Logout();
            case "h":
            case "help":
                return new Command.Help();
            case "j":
            case "join":
                return One(args, "join <#alias|!id>", a => new Command.Join(a));
            case "leave":
            case "part":
                return new Command.Leave();
            case "accept":
                return new Command.Accept();
            case "decline":
            case "reject":
                return new Command.Decline();
            case "invite":
                return OneUser(args, "invite <@user>", u => new Command.Invite(u));
            case "kick":
            case "remove":
                return UserWithReason(args, rest, "kick <@user> [reason]", (u, r) => new Command.Kick(u, r));
            case "ban":
                return UserWithReason(args, rest, "ban <@user> [reason]", (u, r) => new Command.Ban(u, r));
            case "unban":
                return OneUser(args, "unban <@user>", u => new Command.Unban(u));
            case "create":
                return ParseCreate(args);
            case "dm":
            case "query":
            case "msg":
                return OneUser(args, "dm <@user>", u => new Command.DirectMessage(u));
            case "members":
            case "who":
                return new Command.Members();
            case "topic":
                return new Command.Topic(rest.Length == 0 ? null : rest);
            case "name":
            case "rename":
                return rest.Length == 0 ? Usage("name <name>") : new Command.RoomName(rest);
            case "me":
                return rest.Length == 0 ? Usage("me <text>") : new Command.Emote(rest);
            case "react":
                return One(args, "react <emoji>", a => new Command.React(a));
            case "upload":
            case "attach":
                return rest.Length == 0 ? Usage("upload <path>") : new Command.Upload(ExpandHome(Unquote(rest)));
            case "open":
                return new Command.Open();
            case "save":
                return new Command.Save(rest.Length == 0 ? null : ExpandHome(Unquote(rest)));
            case "read":
                return new Command.MarkRead();
            case "ignore":
                return OneUser(args, "ignore <@user>", u => new Command.Ignore(u));
            case "unignore":
                return OneUser(args, "unignore <@user>", u => new Command.Unignore(u));
            case "verify":
                return args.Count switch
                {
                    0 => new Command.Verify(),
                    1 => OneUser(args, "verify [@user]", u => new Command.VerifyUser(u)),
                    _ => Usage("verify [@user]"),
                };
            case "recovery":
            case "backup":
                return ParseRecovery(args, rest);
            case "notifications":
            case "notify":
                return args.Count switch
                {
                    0 => new Command.Notifications(null),
                    1 when args[0] is "on" or "true" or "enable" => new Command.Notifications(true),
                    1 when args[0] is "off" or "false" or "disable" => new Command.Notifications(false),
                    _ => Usage("notifications [on|off]"),
                };
            case "search":
            case "filter":
                return new Command.Search(rest.Length == 0 ? null : rest);
            default:
                return ParsedCommand.Fail($"Unknown command \"{name}\", :help lists the commands.");
        }
    }

    /// <summary>
    /// Completes the command name at the start of <paramref name="line"/>, null if no command starts with it. Several
    /// matches complete to their common prefix.
    /// </summary>
    public static string? Complete(string line)
    {
        if (line.Contains(' ', StringComparison.Ordinal))
        {
            return null;
        }
        string[] matches =
        [
            .. Help.Select(h => h.Name).Where(n => n.StartsWith(line, StringComparison.OrdinalIgnoreCase)),
        ];
        if (matches.Length == 0)
        {
            return null;
        }
        if (matches.Length == 1)
        {
            return matches[0] + " ";
        }
        string prefix = matches[0];
        foreach (string match in matches.Skip(1))
        {
            int length = 0;
            while (length < prefix.Length && length < match.Length && prefix[length] == match[length])
            {
                length++;
            }
            prefix = prefix[..length];
        }
        return prefix;
    }

    /// <summary>
    /// Whether <paramref name="userId"/> looks like a full Matrix user id (<c>@user:server</c>).
    /// </summary>
    public static bool IsUserId(string userId) =>
        userId.Length > 3
        && userId[0] == '@'
        && userId.IndexOf(':', StringComparison.Ordinal) > 1
        && !userId.EndsWith(':');

    private static ParsedCommand ParseCreate(List<string> args)
    {
        bool isPublic = false;
        bool isEncrypted = true;
        List<string> nameParts = [];
        foreach (string arg in args)
        {
            switch (arg)
            {
                case "--public":
                    isPublic = true;
                    break;
                case "--unencrypted":
                    isEncrypted = false;
                    break;
                default:
                    nameParts.Add(arg);
                    break;
            }
        }
        return nameParts.Count == 0
            ? Usage("create <name> [--public] [--unencrypted]")
            : new Command.CreateRoom(string.Join(' ', nameParts), isEncrypted, isPublic);
    }

    private static ParsedCommand ParseRecovery(List<string> args, string rest) =>
        args.Count switch
        {
            0 => new Command.RecoveryStatus(),
            1 when args[0] == "enable" => new Command.EnableRecovery(),
            1 when args[0] == "reset" => new Command.ResetRecoveryKey(),
            // recovery keys are shown in groups of four characters separated by spaces
            _ => new Command.Recover(rest),
        };

    private static ParsedCommand One(List<string> args, string usage, Func<string, Command> create) =>
        args.Count == 1 ? create(args[0]) : Usage(usage);

    private static ParsedCommand OneUser(List<string> args, string usage, Func<string, Command> create)
    {
        if (args.Count != 1)
        {
            return Usage(usage);
        }
        return IsUserId(args[0])
            ? create(args[0])
            : ParsedCommand.Fail($"\"{args[0]}\" isn't a user id, they look like @user:example.org.");
    }

    private static ParsedCommand UserWithReason(
        List<string> args,
        string rest,
        string usage,
        Func<string, string?, Command> create
    )
    {
        if (args.Count == 0)
        {
            return Usage(usage);
        }
        if (!IsUserId(args[0]))
        {
            return ParsedCommand.Fail($"\"{args[0]}\" isn't a user id, they look like @user:example.org.");
        }
        string reason = rest[rest.IndexOf(args[0], StringComparison.Ordinal)..][args[0].Length..].Trim();
        return create(args[0], reason.Length == 0 ? null : reason);
    }

    private static ParsedCommand Usage(string usage) => ParsedCommand.Fail($"Usage: :{usage}");

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    private static string ExpandHome(string path) =>
        path == "~" || path.StartsWith("~/", StringComparison.Ordinal)
            ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[1..])
            : path;

    private static List<string> Split(string rest)
    {
        List<string> args = [];
        StringBuilder current = new();
        bool quoted = false;
        bool hasArg = false;
        foreach (char c in rest)
        {
            if (c == '"')
            {
                quoted = !quoted;
                hasArg = true;
            }
            else if (c == ' ' && !quoted)
            {
                if (hasArg)
                {
                    args.Add(current.ToString());
                    current.Clear();
                    hasArg = false;
                }
            }
            else
            {
                current.Append(c);
                hasArg = true;
            }
        }
        if (hasArg)
        {
            args.Add(current.ToString());
        }
        return args;
    }
}

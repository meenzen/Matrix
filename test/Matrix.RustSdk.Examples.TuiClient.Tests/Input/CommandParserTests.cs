using Matrix.RustSdk.Examples.TuiClient.Input;

namespace Matrix.RustSdk.Examples.TuiClient.Tests.Input;

public class CommandParserTests
{
    public static IEnumerable<Func<(string Line, Command Expected)>> Commands()
    {
        yield return () => ("q", new Command.Quit());
        yield return () => (":quit", new Command.Quit());
        yield return () => ("join #room:example.org", new Command.Join("#room:example.org"));
        yield return () => ("invite @alice:example.org", new Command.Invite("@alice:example.org"));
        yield return () =>
            ("kick @bob:example.org too   much spam", new Command.Kick("@bob:example.org", "too   much spam"));
        yield return () => ("ban @bob:example.org", new Command.Ban("@bob:example.org", null));
        yield return () =>
            ("create \"Book club\"", new Command.CreateRoom("Book club", IsEncrypted: true, IsPublic: false));
        yield return () =>
            (
                "create Lobby --public --unencrypted",
                new Command.CreateRoom("Lobby", IsEncrypted: false, IsPublic: true)
            );
        yield return () => ("dm @alice:example.org", new Command.DirectMessage("@alice:example.org"));
        yield return () => ("topic", new Command.Topic(null));
        yield return () => ("topic All about  books", new Command.Topic("All about  books"));
        yield return () => ("me waves", new Command.Emote("waves"));
        yield return () => ("react 👍", new Command.React("👍"));
        yield return () => ("upload \"/home/me/a b.png\"", new Command.Upload("/home/me/a b.png"));
        yield return () => ("save", new Command.Save(null));
        yield return () => ("recovery", new Command.RecoveryStatus());
        yield return () => ("recovery enable", new Command.EnableRecovery());
        yield return () => ("recovery EsTc abcd 1234", new Command.Recover("EsTc abcd 1234"));
        yield return () => ("verify", new Command.Verify());
        yield return () => ("verify @alice:example.org", new Command.VerifyUser("@alice:example.org"));
        yield return () => ("notifications off", new Command.Notifications(false));
    }

    [Test]
    [MethodDataSource(nameof(Commands))]
    public async Task Parse_ShouldParseTheCommand(string line, Command expected)
    {
        // Act
        ParsedCommand result = CommandParser.Parse(line);

        // Assert
        await Assert.That(result.Error).IsNull();
        await Assert.That(result.Command).IsEqualTo(expected);
    }

    [Test]
    [Arguments("", "No command.")]
    [Arguments("frobnicate", "Unknown command \"frobnicate\", :help lists the commands.")]
    [Arguments("join", "Usage: :join <#alias|!id>")]
    [Arguments("invite alice", "\"alice\" isn't a user id, they look like @user:example.org.")]
    [Arguments("create --public", "Usage: :create <name> [--public] [--unencrypted]")]
    public async Task Parse_ShouldExplainErrors(string line, string error)
    {
        // Act
        ParsedCommand result = CommandParser.Parse(line);

        // Assert
        await Assert.That(result.Command).IsNull();
        await Assert.That(result.Error).IsEqualTo(error);
    }

    [Test]
    [Arguments("inv", "invite ")]
    [Arguments("rea", "rea")]
    [Arguments("reac", "react ")]
    [Arguments("xyz", null)]
    [Arguments("join #a", null)]
    public async Task Complete_ShouldCompleteCommandNames(string line, string? expected)
    {
        // Act
        string? completed = CommandParser.Complete(line);

        // Assert
        await Assert.That(completed).IsEqualTo(expected);
    }
}

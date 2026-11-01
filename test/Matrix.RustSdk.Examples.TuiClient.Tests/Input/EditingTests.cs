using Matrix.RustSdk.Examples.TuiClient.Input;
using Matrix.RustSdk.Testing;
using Terminal.Gui.Input;
using Command = Matrix.RustSdk.Examples.TuiClient.Input.Command;

namespace Matrix.RustSdk.Examples.TuiClient.Tests.Input;

/// <summary>
/// The readline keys, completion of arguments, shortcodes and links.
/// </summary>
public class EditingTests
{
    [Test]
    public async Task Map_ShouldMapTheEditingKeysInInsertMode()
    {
        // Arrange
        VimKeymap keymap = new();

        // Assert
        await Assert.That(keymap.Map(InputMode.Insert, Key.Tab)).IsEqualTo(new KeyAction(InputAction.Complete));
        await Assert
            .That(keymap.Map(InputMode.Insert, Key.W.WithCtrl))
            .IsEqualTo(new KeyAction(InputAction.DeleteWord));
        await Assert
            .That(keymap.Map(InputMode.Insert, Key.U.WithCtrl))
            .IsEqualTo(new KeyAction(InputAction.DeleteLine));
        await Assert.That(keymap.Map(InputMode.Insert, Key.C.WithCtrl)).IsEqualTo(new KeyAction(InputAction.Normal));
        await Assert
            .That(keymap.Map(InputMode.Command, Key.Backspace))
            .IsEqualTo(new KeyAction(InputAction.DeleteBack));
        await Assert.That(keymap.Map(InputMode.Command, Key.C.WithCtrl)).IsEqualTo(new KeyAction(InputAction.Cancel));
    }

    [Test]
    public async Task Map_ShouldTellACountFromNone()
    {
        // Arrange
        VimKeymap keymap = new();

        // Act
        KeyResult last = keymap.Map(InputMode.Normal, new Key('G'));
        keymap.Map(InputMode.Normal, new Key('5'));
        KeyResult fifth = keymap.Map(InputMode.Normal, new Key('G'));

        // Assert
        await Assert.That(last).IsEqualTo(new KeyAction(InputAction.MoveLast));
        await Assert.That(fifth).IsEqualTo(new KeyAction(InputAction.MoveLast, 5, HasCount: true));
    }

    [Test]
    [Arguments("ab cd", 5, 3)]
    [Arguments("ab cd  ", 7, 3)]
    [Arguments("ab cd", 3, 0)]
    [Arguments("word", 0, 0)]
    public async Task WordStart_ShouldFindTheWordBeforeTheCursor(string text, int cursor, int expected)
    {
        // Act
        int start = TextEditing.WordStart(text, cursor);

        // Assert
        await Assert.That(start).IsEqualTo(expected);
    }

    [Test]
    [Arguments("invite @al", "invite @alice:example.org ")]
    [Arguments("dm al", "dm @alice:example.org ")]
    [Arguments("kick @b", "kick @bob")]
    [Arguments("invite @zed", null)]
    [Arguments("react +", "react +1")]
    [Arguments("react tad", "react tada")]
    [Arguments("topic al", null)]
    public async Task Complete_ShouldCompleteArguments(string line, string? expected)
    {
        // Arrange
        string[] users = ["@alice:example.org", "@bob:example.org", "@bobby:example.org"];

        // Act
        string? completed = CommandParser.Complete(line, users);

        // Assert
        await Assert.That(completed).IsEqualTo(expected);
    }

    [Test]
    public async Task Complete_ShouldCompletePaths()
    {
        // Arrange
        using TemporaryDirectory directory = new();
        Directory.CreateDirectory(Path.Join(directory.Path, "photos"));
        await File.WriteAllTextAsync(Path.Join(directory.Path, "notes.txt"), "");

        // Act
        string? file = CommandParser.Complete($"upload {directory.Path}/no");
        string? folder = CommandParser.Complete($"upload {directory.Path}/ph");

        // Assert
        await Assert.That(file).IsEqualTo($"upload {directory.Path}/notes.txt");
        await Assert.That(folder).IsEqualTo($"upload {directory.Path}/photos/");
    }

    [Test]
    [Arguments("+1", "👍")]
    [Arguments(":tada:", "🎉")]
    [Arguments("HEART", "❤️")]
    [Arguments("🚀", "🚀")]
    [Arguments("lol", "lol")]
    public async Task Shortcodes_ShouldResolveToEmoji(string key, string expected)
    {
        // Act
        string emoji = Shortcodes.Resolve(key);

        // Assert
        await Assert.That(emoji).IsEqualTo(expected);
    }

    [Test]
    public async Task Parse_ShouldParseLinksAndRetry()
    {
        // Assert
        await Assert.That(CommandParser.Parse("open 2").Command).IsEqualTo(new Command.Open(2));
        await Assert.That(CommandParser.Parse("open").Command).IsEqualTo(new Command.Open());
        await Assert.That(CommandParser.Parse("open x").Error).IsEqualTo("Usage: :open [n]");
        await Assert.That(CommandParser.Parse("retry").Command).IsEqualTo(new Command.Retry());
        await Assert.That(CommandParser.Parse("react +1").Command).IsEqualTo(new Command.React("👍"));
    }

    [Test]
    public async Task Links_ShouldFindTheLinksOfAMessage()
    {
        // Act
        IReadOnlyList<string> links = TextEditing.Links(
            "see https://example.org/a?b=c, and (https://matrix.org). Not ftp://x"
        );

        // Assert
        await Assert.That(links).IsEquivalentTo(["https://example.org/a?b=c", "https://matrix.org"]);
    }
}

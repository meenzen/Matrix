using Matrix.RustSdk.Examples.TuiClient.Input;
using Terminal.Gui.Input;

namespace Matrix.RustSdk.Examples.TuiClient.Tests.Input;

public class VimKeymapTests
{
    [Test]
    [Arguments('j', InputAction.MoveDown)]
    [Arguments('k', InputAction.MoveUp)]
    [Arguments('G', InputAction.MoveLast)]
    [Arguments('h', InputAction.FocusRooms)]
    [Arguments('l', InputAction.FocusTimeline)]
    [Arguments('i', InputAction.Insert)]
    [Arguments(':', InputAction.Command)]
    [Arguments('/', InputAction.Filter)]
    [Arguments('r', InputAction.Reply)]
    [Arguments('?', InputAction.Help)]
    public async Task Map_ShouldMapSingleKeysInNormalMode(char key, InputAction expected)
    {
        // Arrange
        VimKeymap keymap = new();

        // Act
        KeyResult result = keymap.Map(InputMode.Normal, new Key(key));

        // Assert
        await Assert.That(result).IsEqualTo(new KeyAction(expected));
    }

    [Test]
    public async Task Map_ShouldWaitForTheSecondKeyOfASequence()
    {
        // Arrange
        VimKeymap keymap = new();

        // Act
        KeyResult first = keymap.Map(InputMode.Normal, new Key('g'));
        string pending = keymap.PendingKeys;
        KeyResult second = keymap.Map(InputMode.Normal, new Key('g'));

        // Assert
        await Assert.That(first).IsEqualTo(KeyResult.Pending);
        await Assert.That(pending).IsEqualTo("g");
        await Assert.That(second).IsEqualTo(new KeyAction(InputAction.MoveFirst));
        await Assert.That(keymap.PendingKeys).IsEmpty();
    }

    [Test]
    public async Task Map_ShouldApplyACount()
    {
        // Arrange
        VimKeymap keymap = new();

        // Act
        keymap.Map(InputMode.Normal, new Key('1'));
        keymap.Map(InputMode.Normal, new Key('2'));
        KeyResult result = keymap.Map(InputMode.Normal, new Key('j'));

        // Assert
        await Assert.That(result).IsEqualTo(new KeyAction(InputAction.MoveDown, 12, HasCount: true));
    }

    [Test]
    public async Task Map_ShouldIgnoreAnUnknownSequence()
    {
        // Arrange
        VimKeymap keymap = new();
        keymap.Map(InputMode.Normal, new Key('d'));

        // Act
        KeyResult result = keymap.Map(InputMode.Normal, new Key('x'));

        // Assert: the unfinished dd is gone, the next d starts over
        await Assert.That(result).IsEqualTo(KeyResult.Ignored);
        await Assert.That(keymap.Map(InputMode.Normal, new Key('d'))).IsEqualTo(KeyResult.Pending);
    }

    [Test]
    public async Task Map_ShouldMapControlKeys()
    {
        // Arrange
        VimKeymap keymap = new();

        // Act
        KeyResult result = keymap.Map(InputMode.Normal, Key.D.WithCtrl);

        // Assert
        await Assert.That(result).IsEqualTo(new KeyAction(InputAction.HalfPageDown));
    }

    [Test]
    public async Task Map_ShouldPassTypedKeysThroughInInsertMode()
    {
        // Arrange
        VimKeymap keymap = new();

        // Act
        KeyResult typed = keymap.Map(InputMode.Insert, new Key('j'));
        KeyResult escape = keymap.Map(InputMode.Insert, Key.Esc);
        KeyResult enter = keymap.Map(InputMode.Insert, Key.Enter);
        KeyResult altEnter = keymap.Map(InputMode.Insert, Key.Enter.WithAlt);

        // Assert
        await Assert.That(typed).IsEqualTo(KeyResult.PassThrough);
        await Assert.That(escape).IsEqualTo(new KeyAction(InputAction.Normal));
        await Assert.That(enter).IsEqualTo(new KeyAction(InputAction.Send));
        await Assert.That(altEnter).IsEqualTo(new KeyAction(InputAction.NewLine));
    }

    [Test]
    [Arguments(InputMode.Command)]
    [Arguments(InputMode.Filter)]
    public async Task Map_ShouldEditTheLineInCommandAndFilterMode(InputMode mode)
    {
        // Arrange
        VimKeymap keymap = new();

        // Act
        KeyResult typed = keymap.Map(mode, new Key('q'));
        KeyResult enter = keymap.Map(mode, Key.Enter);
        KeyResult escape = keymap.Map(mode, Key.Esc);

        // Assert
        await Assert.That(typed).IsEqualTo(KeyResult.PassThrough);
        await Assert.That(enter).IsEqualTo(new KeyAction(InputAction.Execute));
        await Assert.That(escape).IsEqualTo(new KeyAction(InputAction.Cancel));
    }
}

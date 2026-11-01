using Matrix.RustSdk.Examples.TuiClient.Rendering;

namespace Matrix.RustSdk.Examples.TuiClient.Tests.Rendering;

public class TextLayoutTests
{
    [Test]
    public async Task Wrap_ShouldBreakAtSpaces()
    {
        // Act
        IReadOnlyList<string> lines = TextLayout.Wrap("the quick brown fox jumps", 10);

        // Assert
        await Assert.That(lines).IsEquivalentTo(["the quick", "brown fox", "jumps"]);
    }

    [Test]
    public async Task Wrap_ShouldBreakLongWords()
    {
        // Act
        IReadOnlyList<string> lines = TextLayout.Wrap("abcdefghijkl xy", 5);

        // Assert
        await Assert.That(lines).IsEquivalentTo(["abcde", "fghij", "kl xy"]);
    }

    [Test]
    public async Task Wrap_ShouldKeepLineBreaks()
    {
        // Act
        IReadOnlyList<string> lines = TextLayout.Wrap("one\r\ntwo\n\nthree", 20);

        // Assert
        await Assert.That(lines).IsEquivalentTo(["one", "two", "", "three"]);
    }

    [Test]
    public async Task Wrap_ShouldCountWideCharactersAsTwoColumns()
    {
        // Act: every CJK character takes two columns
        IReadOnlyList<string> lines = TextLayout.Wrap("日本語のテキスト", 6);

        // Assert
        await Assert.That(lines).IsEquivalentTo(["日本語", "のテキ", "スト"]);
        await Assert.That(lines.All(l => TextLayout.Width(l) <= 6)).IsTrue();
    }

    [Test]
    public async Task Wrap_ShouldNotSplitEmojiSequences()
    {
        // Arrange: a family emoji is one grapheme made of several code points
        const string family = "👨\u200D👩\u200D👧";

        // Act
        IReadOnlyList<string> lines = TextLayout.Wrap($"a{family}{family}", 3);

        // Assert
        await Assert.That(lines).IsEquivalentTo([$"a{family}", family]);
    }

    [Test]
    [Arguments("short", 10, "short")]
    [Arguments("a long room name", 8, "a long …")]
    [Arguments("anything", 0, "")]
    public async Task Truncate_ShouldEndWithAnEllipsis(string text, int width, string expected)
    {
        // Act
        string truncated = TextLayout.Truncate(text, width);

        // Assert
        await Assert.That(truncated).IsEqualTo(expected);
    }
}

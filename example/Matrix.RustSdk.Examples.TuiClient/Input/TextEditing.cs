using System.Text.RegularExpressions;

namespace Matrix.RustSdk.Examples.TuiClient.Input;

/// <summary>
/// Text helpers of the composer and the command line.
/// </summary>
public static partial class TextEditing
{
    /// <summary>
    /// Where the word before <paramref name="cursor"/> starts, after the spaces before the cursor, for C-w.
    /// </summary>
    public static int WordStart(string text, int cursor)
    {
        int start = cursor;
        while (start > 0 && char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }
        while (start > 0 && !char.IsWhiteSpace(text[start - 1]))
        {
            start--;
        }
        return start;
    }

    /// <summary>
    /// The links in a message, in the order they appear, without the punctuation that usually follows them.
    /// </summary>
    public static IReadOnlyList<string> Links(string text) =>
        [
            .. LinkPattern()
                .Matches(text)
                .Select(m => m.Value.TrimEnd('.', ',', ')', '!', '?', ';', ':', '>', '"', '\'')),
        ];

    [GeneratedRegex(@"https?://\S+", RegexOptions.IgnoreCase)]
    private static partial Regex LinkPattern();
}

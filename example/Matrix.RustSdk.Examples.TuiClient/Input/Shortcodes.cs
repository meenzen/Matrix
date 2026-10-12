namespace Matrix.RustSdk.Examples.TuiClient.Input;

/// <summary>
/// Emoji for the shortcodes reactions are usually given with (<c>:react +1</c>, <c>:react :tada:</c>), terminals make
/// typing emoji hard.
/// </summary>
public static class Shortcodes
{
    private static readonly Dictionary<string, string> Emoji = new(StringComparer.OrdinalIgnoreCase)
    {
        ["+1"] = "👍",
        ["thumbsup"] = "👍",
        ["-1"] = "👎",
        ["thumbsdown"] = "👎",
        ["heart"] = "❤️",
        ["tada"] = "🎉",
        ["joy"] = "😂",
        ["laughing"] = "😆",
        ["smile"] = "😄",
        ["slightly_smiling_face"] = "🙂",
        ["wink"] = "😉",
        ["thinking"] = "🤔",
        ["eyes"] = "👀",
        ["ok_hand"] = "👌",
        ["ok"] = "🆗",
        ["check"] = "✅",
        ["white_check_mark"] = "✅",
        ["x"] = "❌",
        ["rocket"] = "🚀",
        ["fire"] = "🔥",
        ["clap"] = "👏",
        ["pray"] = "🙏",
        ["wave"] = "👋",
        ["cry"] = "😢",
        ["sob"] = "😭",
        ["open_mouth"] = "😮",
        ["scream"] = "😱",
        ["100"] = "💯",
        ["star"] = "⭐",
        ["sparkles"] = "✨",
        ["muscle"] = "💪",
        ["coffee"] = "☕",
        ["beer"] = "🍺",
        ["party"] = "🥳",
        ["shrug"] = "🤷",
        ["facepalm"] = "🤦",
        ["see_no_evil"] = "🙈",
        ["raised_hands"] = "🙌",
        ["heart_eyes"] = "😍",
        ["sweat_smile"] = "😅",
        ["upside_down"] = "🙃",
    };

    /// <summary>
    /// The shortcodes, for completion.
    /// </summary>
    public static IEnumerable<string> Names => Emoji.Keys;

    /// <summary>
    /// The emoji of a shortcode (with or without the colons), the key itself if it isn't one: it may already be an
    /// emoji, or any text, which reactions allow.
    /// </summary>
    public static string Resolve(string key)
    {
        string name = key.Length > 2 && key[0] == ':' && key[^1] == ':' ? key[1..^1] : key;
        return Emoji.GetValueOrDefault(name, key);
    }
}

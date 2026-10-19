using System.ComponentModel.DataAnnotations;

namespace Matrix.RustSdk.Examples.EchoBot;

/// <summary>
/// The account of the bot, bound from the <c>EchoBot</c> configuration section.
/// </summary>
public sealed class EchoBotOptions
{
    public const string SectionName = "EchoBot";

    /// <summary>
    /// The server name (<c>matrix.org</c>) or the URL of the homeserver (<c>https://matrix-client.matrix.org</c>).
    /// </summary>
    [Required]
    public string Homeserver { get; set; } = "";

    [Required]
    public string Username { get; set; } = "";

    /// <summary>
    /// Only needed for the first login, later starts restore the session stored in <see cref="DataDirectory"/>.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// The name of the device the bot creates when it logs in.
    /// </summary>
    public string DeviceName { get; set; } = "Echo Bot";

    /// <summary>
    /// Where the bot stores its session and the stores of the SDK, one directory per account.
    /// </summary>
    [Required]
    public string DataDirectory { get; set; } = "data";
}

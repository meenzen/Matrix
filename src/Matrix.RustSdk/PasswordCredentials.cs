namespace Matrix.RustSdk;

/// <summary>
/// The account a <see cref="StoredClient"/> logs in to with a password. The properties can be bound from configuration.
/// </summary>
public sealed class PasswordCredentials
{
    /// <summary>
    /// The server name (<c>matrix.org</c>) or the URL of the homeserver (<c>https://matrix-client.matrix.org</c>).
    /// Required, but only used to log in: a restore uses the homeserver URL stored with the session.
    /// </summary>
    public string Homeserver { get; set; } = "";

    /// <summary>
    /// The localpart (<c>alice</c>) or the full user id (<c>@alice:matrix.org</c>). Required, a stored session has to
    /// belong to it (with a localpart the server isn't compared).
    /// </summary>
    public string Username { get; set; } = "";

    /// <summary>
    /// The password, only needed when the client has to log in. <see cref="StoredClient.LoginOrRestoreAsync"/> doesn't
    /// need it while a session is stored.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// The display name of the device the login creates, visible to other users.
    /// </summary>
    public string? DeviceName { get; set; }
}

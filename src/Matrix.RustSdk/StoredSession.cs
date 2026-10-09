using System.Text.Json.Serialization;
using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk;

/// <summary>
/// The state of the session in a data directory, see <see cref="SessionFile"/>.
/// </summary>
internal enum SessionState
{
    /// <summary>
    /// A login with <see cref="SessionFile.DeviceId"/> was started, there is no session yet. See
    /// <see cref="SessionFile.DeviceMayExist"/>.
    /// </summary>
    Pending,

    /// <summary>
    /// Logged in, <see cref="SessionFile.Session"/> can be restored.
    /// </summary>
    Active,

    /// <summary>
    /// The homeserver ended the session but keeps the device (soft logout): a login with the same device id continues
    /// with the same store.
    /// </summary>
    SoftLoggedOut,

    /// <summary>
    /// Logged out or the homeserver deleted the device: the store belongs to a device that is gone and is deleted the
    /// next time the directory is opened.
    /// </summary>
    LoggedOut,
}

/// <summary>
/// The content of <c>session.json</c>. Written before the login (<see cref="SessionState.Pending"/>), so a login that
/// reached the server but didn't finish reuses its device and store, and after it with the session.
/// </summary>
internal sealed record SessionFile
{
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public SessionState State { get; init; }

    /// <summary>
    /// The homeserver of a pending login as the caller passed it.
    /// </summary>
    public string? Homeserver { get; init; }

    /// <summary>
    /// The username of a pending login as the caller passed it.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// The device id the login uses, generated before the first attempt.
    /// </summary>
    public required string DeviceId { get; init; }

    /// <summary>
    /// Whether a login request of a pending login may have created the device: set right before the first request,
    /// it stays set unless the homeserver rejected every request. While it is set the store may contain the keys of
    /// the device and only the same account may log in.
    /// </summary>
    public bool DeviceMayExist { get; init; }

    public StoredSession? Session { get; init; }
}

/// <summary>
/// A <see cref="Bindings.Session"/> in <c>session.json</c>, with names of its own so SDK updates don't change the file
/// format.
/// </summary>
internal sealed record StoredSession(
    string AccessToken,
    string? RefreshToken,
    string UserId,
    string DeviceId,
    string HomeserverUrl,
    string? OauthData,
    SlidingSyncVersion SlidingSyncVersion
)
{
    public static StoredSession From(Session session) =>
        new(
            session.AccessToken,
            session.RefreshToken,
            session.UserId,
            session.DeviceId,
            session.HomeserverUrl,
            session.OauthData,
            session.SlidingSyncVersion
        );

    public Session ToSession() =>
        new(AccessToken, RefreshToken, UserId, DeviceId, HomeserverUrl, OauthData, SlidingSyncVersion);
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true
)]
[JsonSerializable(typeof(SessionFile))]
internal sealed partial class SessionFileJsonContext : JsonSerializerContext;

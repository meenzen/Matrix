namespace Matrix.RustSdk.Examples.TuiClient;

/// <summary>
/// The login details known before the login form is shown. Missing values are asked for in the form, if all of them
/// are known the client logs in right away. The session is stored in <see cref="DataDirectory"/>, the client only shows
/// the login form if there is no session to restore.
/// </summary>
public sealed record LoginOptions(string? Homeserver, string? Username, string? Password, string DataDirectory)
{
    /// <summary>
    /// The data directory if neither <c>--data-directory</c> nor <c>MATRIX_DATA_DIRECTORY</c> is set:
    /// <c>~/.local/share/Matrix.RustSdk.TuiClient</c> on Linux, the local app data on Windows and macOS.
    /// </summary>
    public static string DefaultDataDirectory =>
        Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Matrix.RustSdk.TuiClient"
        );

    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Homeserver)
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrEmpty(Password);

    /// <summary>
    /// Reads <c>--homeserver</c>, <c>--username</c> and <c>--data-directory</c> from the command line, falling back to
    /// the <c>MATRIX_HOMESERVER</c>, <c>MATRIX_USERNAME</c> and <c>MATRIX_DATA_DIRECTORY</c> environment variables. The
    /// password is only read from <c>MATRIX_PASSWORD</c>, command line arguments end up in the shell history.
    /// </summary>
    public static LoginOptions Parse(string[] args)
    {
        string? homeserver = Environment.GetEnvironmentVariable("MATRIX_HOMESERVER");
        string? username = Environment.GetEnvironmentVariable("MATRIX_USERNAME");
        string? password = Environment.GetEnvironmentVariable("MATRIX_PASSWORD");
        string? dataDirectory = Environment.GetEnvironmentVariable("MATRIX_DATA_DIRECTORY");

        for (int i = 0; i < args.Length - 1; i++)
        {
            string value = args[i + 1];
            switch (args[i])
            {
                case "--homeserver":
                    homeserver = value;
                    break;
                case "--username":
                    username = value;
                    break;
                case "--data-directory":
                    dataDirectory = value;
                    break;
            }
        }

        return new LoginOptions(
            homeserver,
            username,
            password,
            string.IsNullOrWhiteSpace(dataDirectory) ? DefaultDataDirectory : dataDirectory
        );
    }
}

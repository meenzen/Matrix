namespace Matrix.RustSdk.Examples.TuiClient;

/// <summary>
/// The login details known before the login form is shown. Missing values are asked for in the form, if all of them
/// are known the client logs in right away.
/// </summary>
public sealed record LoginOptions(string? Homeserver, string? Username, string? Password)
{
    public bool IsComplete =>
        !string.IsNullOrWhiteSpace(Homeserver)
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrEmpty(Password);

    /// <summary>
    /// Reads <c>--homeserver</c> and <c>--username</c> from the command line, falling back to the
    /// <c>MATRIX_HOMESERVER</c> and <c>MATRIX_USERNAME</c> environment variables. The password is only read from
    /// <c>MATRIX_PASSWORD</c>, command line arguments end up in the shell history.
    /// </summary>
    public static LoginOptions Parse(string[] args)
    {
        string? homeserver = Environment.GetEnvironmentVariable("MATRIX_HOMESERVER");
        string? username = Environment.GetEnvironmentVariable("MATRIX_USERNAME");
        string? password = Environment.GetEnvironmentVariable("MATRIX_PASSWORD");

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
            }
        }

        return new LoginOptions(homeserver, username, password);
    }
}

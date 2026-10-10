using Matrix.RustSdk;
using Matrix.RustSdk.Examples.TuiClient;
using Terminal.Gui.App;

if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(
        """
        Matrix TUI client

        Usage: Matrix.RustSdk.Examples.TuiClient [options]

          --homeserver <url>        the homeserver to log in to (MATRIX_HOMESERVER)
          --username <name>         the user to log in as (MATRIX_USERNAME), the password is read from
                                    MATRIX_PASSWORD or asked for
          --data-directory <path>   where the session, the keys and the logs are stored (MATRIX_DATA_DIRECTORY),
                                    by default the local application data directory
          --no-notifications        start without desktop notifications, :notifications turns them on

        Press ? in the client for the keys and commands.
        """
    );
    return;
}

LoginOptions options = LoginOptions.Parse(args);

// once per process, the logs go to a file: the console belongs to the UI
MatrixSdk.Initialize(new MatrixSdkOptions { LogDirectory = Path.Join(options.DataDirectory, "logs") });

using IApplication app = Application.Create().Init();
await TuiClient.RunAsync(
    app,
    options,
    new ClientSettings { NotificationsEnabled = !args.Contains("--no-notifications") }
);

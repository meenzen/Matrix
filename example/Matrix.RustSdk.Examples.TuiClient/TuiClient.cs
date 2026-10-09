using Terminal.Gui.App;

namespace Matrix.RustSdk.Examples.TuiClient;

public static class TuiClient
{
    /// <summary>
    /// Runs the client on the initialized <paramref name="app"/> until the user quits or
    /// <paramref name="cancellationToken"/> is cancelled: the login window first, then the chat window.
    /// </summary>
    public static async Task RunAsync(
        IApplication app,
        LoginOptions options,
        CancellationToken cancellationToken = default
    )
    {
        MatrixSession? session;
        using (LoginWindow loginWindow = new(options))
        {
            // runs the main loop on the calling thread until the window is closed
            await app.RunAsync(loginWindow, cancellationToken);
            session = loginWindow.Session;
        }
        if (session is null)
        {
            return;
        }

        await using (session)
        {
            await using ChatWindow chatWindow = new(app, session);
            await app.RunAsync(chatWindow, cancellationToken);
        }
    }
}

using Matrix.RustSdk.Bindings;
using Terminal.Gui.App;

namespace Matrix.RustSdk.Examples.TuiClient;

public static class TuiClient
{
    /// <summary>
    /// Runs the client on the initialized <paramref name="app"/> until the user quits or
    /// <paramref name="cancellationToken"/> is cancelled: the chat window with the stored session, the login window
    /// first if there is none. Logging out goes back to the login window.
    /// </summary>
    public static async Task RunAsync(
        IApplication app,
        LoginOptions options,
        CancellationToken cancellationToken = default
    )
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            MatrixSession? session = null;
            string? error = null;
            try
            {
                session = await MatrixSession.TryRestoreAsync(options.DataDirectory);
            }
            catch (Exception e)
            {
                // another instance uses the data directory, a store that can't be opened, ...
                error = $"Restoring the session failed: {e.Message}";
            }
            session ??= await LoginAsync(app, options, error, cancellationToken);
            if (session is null)
            {
                return;
            }

            bool logout;
            await using (ChatWindow chatWindow = new(app, session))
            {
                await app.RunAsync(chatWindow, cancellationToken);
                logout = chatWindow.IsLogoutRequested;
            }
            if (!logout)
            {
                await session.DisposeAsync();
                return;
            }

            try
            {
                await session.LogoutAsync();
            }
            catch (ClientException)
            {
                // the homeserver can't be reached, the session is still stored and restored again
                continue;
            }
            // the password was used, the login form asks for it again
            options = options with
            {
                Password = null,
            };
        }
    }

    private static async Task<MatrixSession?> LoginAsync(
        IApplication app,
        LoginOptions options,
        string? error,
        CancellationToken cancellationToken
    )
    {
        using LoginWindow loginWindow = new(options, error);
        // runs the main loop on the calling thread until the window is closed
        await app.RunAsync(loginWindow, cancellationToken);
        return loginWindow.Session;
    }
}

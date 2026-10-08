using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Matrix.RustSdk.Examples.TuiClient;

/// <summary>
/// Asks for the homeserver, username and password and logs in. Logs in right away if the options are complete.
/// </summary>
public sealed class LoginWindow : Window
{
    private readonly TextField _homeserver;
    private readonly TextField _username;
    private readonly TextField _password;
    private readonly Label _status;
    private bool _loggingIn;

    public LoginWindow(LoginOptions options)
    {
        Title = "Matrix TUI client - log in (Esc to quit)";

        _homeserver = AddField("Homeserver:", 1, options.Homeserver ?? "matrix.org");
        _username = AddField("Username:", 3, options.Username ?? "");
        _password = AddField("Password:", 5, options.Password ?? "");
        _password.Secret = true;

        Button login = new()
        {
            Text = "_Log in",
            X = 14,
            Y = 7,
            IsDefault = true,
        };
        login.Accepting += OnAccepting;

        _status = new Label
        {
            X = 2,
            Y = 9,
            Width = Dim.Fill(2),
        };
        Add(login, _status);

        // start with the first empty field
        (new[] { _homeserver, _username, _password }.FirstOrDefault(f => f.Text.Length == 0) ?? _password).SetFocus();

        IsRunningChanged += (_, e) =>
        {
            if (e.Value && options.IsComplete)
            {
                StartLogin();
            }
        };
    }

    /// <summary>
    /// The logged in session, null if the window was closed without logging in.
    /// </summary>
    public MatrixSession? Session { get; private set; }

    private TextField AddField(string label, int y, string text)
    {
        TextField field = new()
        {
            X = 14,
            Y = y,
            Width = Dim.Fill(2),
            Text = text,
        };
        // Enter in any field logs in
        field.Accepting += OnAccepting;
        Add(
            new Label
            {
                Text = label,
                X = 2,
                Y = y,
            },
            field
        );
        return field;
    }

    private void OnAccepting(object? sender, CommandEventArgs e)
    {
        e.Handled = true;
        StartLogin();
    }

    private void StartLogin()
    {
        if (!_loggingIn)
        {
            _ = LoginAsync();
        }
    }

    private async Task LoginAsync()
    {
        _loggingIn = true;
        _status.Text = "Logging in...";
        try
        {
            // the continuation runs on the main loop again, Terminal.Gui installs a SynchronizationContext
            MatrixSession session = await MatrixSession.LoginAsync(
                _homeserver.Text.Trim(),
                _username.Text.Trim(),
                _password.Text
            );
            if (!IsRunning)
            {
                // the window was closed while logging in
                await session.DisposeAsync();
                return;
            }
            Session = session;
            RequestStop();
        }
        catch (Exception e)
        {
            _status.Text = $"Login failed: {e.Message}";
        }
        finally
        {
            _loggingIn = false;
        }
    }
}

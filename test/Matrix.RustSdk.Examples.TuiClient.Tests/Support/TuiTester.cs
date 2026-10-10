using System.Collections.Concurrent;
using Matrix.RustSdk.Examples.TuiClient.Notifications;
using Matrix.RustSdk.Testing;
using Terminal.Gui.Input;

namespace Matrix.RustSdk.Examples.TuiClient.Tests.Support;

/// <summary>
/// A TUI client logged in as a user, with its own data directory. Notifications and opened files are recorded instead
/// of reaching the desktop.
/// </summary>
public sealed class TuiTester : IAsyncDisposable
{
    private readonly TemporaryDirectory _data;
    private readonly TemporaryDirectory _downloads;
    private readonly ConcurrentQueue<string> _opened;

    private TuiTester(
        TestUser user,
        TemporaryDirectory data,
        TemporaryDirectory downloads,
        TuiClientRunner tui,
        RecordingNotifier notifier,
        ConcurrentQueue<string> opened
    )
    {
        User = user;
        _data = data;
        _downloads = downloads;
        Tui = tui;
        Notifier = notifier;
        _opened = opened;
    }

    public TestUser User { get; }

    public TuiClientRunner Tui { get; }

    public RecordingNotifier Notifier { get; }

    public string DownloadDirectory => _downloads.Path;

    /// <summary>
    /// The files the client opened with the default application.
    /// </summary>
    public IReadOnlyCollection<string> OpenedFiles => _opened;

    /// <summary>
    /// Starts the client for <paramref name="user"/> (a new one by default), which logs in with the password, and
    /// waits until it syncs.
    /// </summary>
    public static async Task<TuiTester> StartAsync(Homeserver homeserver, TestUser? user = null, string prefix = "tui")
    {
        user ??= await homeserver.CreateUserAsync(prefix);
        TemporaryDirectory data = new();
        TemporaryDirectory downloads = new();
        RecordingNotifier notifier = new();
        ConcurrentQueue<string> opened = new();
        TuiClientRunner tui = await TuiClientRunner.StartAsync(
            new LoginOptions(homeserver.Url, user.Username, user.Password, data.Path),
            new ClientSettings
            {
                Notifier = notifier,
                Opener = path =>
                {
                    opened.Enqueue(path);
                    return true;
                },
                DownloadDirectory = downloads.Path,
            }
        );
        TuiTester tester = new(user, data, downloads, tui, notifier, opened);
        await tester.WaitForTextAsync("sync: running");
        return tester;
    }

    public Task PressAsync(Key key) => Tui.PressAsync(key);

    public Task PressAsync(char key) => Tui.PressAsync(new Key(key));

    /// <summary>
    /// Presses the keys of <paramref name="keys"/> one by one, like typing them.
    /// </summary>
    public Task TypeAsync(string keys) => Tui.TypeAsync(keys);

    /// <summary>
    /// Runs a <c>:</c> command from normal mode.
    /// </summary>
    public Task CommandAsync(string command) => Tui.CommandAsync(command);

    public Task WaitForTextAsync(string text) => Tui.WaitForTextAsync(text);

    public Task WaitForTextGoneAsync(string text) => Tui.WaitForTextGoneAsync(text);

    public Task WaitForMatchAsync(string pattern) => Tui.WaitForMatchAsync(pattern);

    public Task<string> GetScreenAsync() => Tui.GetScreenAsync();

    /// <summary>
    /// Goes back to normal mode from whatever mode the client is in.
    /// </summary>
    public async Task NormalModeAsync()
    {
        await Tui.PressAsync(Key.Esc);
        await Tui.PressAsync(Key.Esc);
    }

    public async ValueTask DisposeAsync()
    {
        await Tui.DisposeAsync();
        _data.Dispose();
        _downloads.Dispose();
    }
}

/// <summary>
/// Records the notifications instead of showing them.
/// </summary>
public sealed class RecordingNotifier : INotifier
{
    private readonly ConcurrentQueue<(string Title, string Body)> _notifications = new();

    public IReadOnlyCollection<(string Title, string Body)> Notifications => _notifications;

    public void Notify(string title, string body) => _notifications.Enqueue((title, body));

    public Task WaitForAsync(Func<(string Title, string Body), bool> predicate, string description) =>
        Poll.UntilAsync(() => Task.FromResult(_notifications.Any(predicate)), description);
}

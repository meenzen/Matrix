using Matrix.RustSdk.Testing;
using Terminal.Gui.App;
using Terminal.Gui.Drivers;
using Terminal.Gui.Input;
using Terminal.Gui.Testing;

namespace Matrix.RustSdk.Examples.TuiClient.Tests;

/// <summary>
/// Runs the real TUI client in-process without a terminal: Terminal.Gui's ANSI driver renders into its screen buffer
/// only, keys are injected into its input processor. Tests type like a user and read the screen.
/// </summary>
public sealed class TuiClientRunner : IAsyncDisposable
{
    private static readonly TimeSpan Timeout = Poll.DefaultTimeout;

    private readonly IApplication _app;
    private readonly CancellationTokenSource _stop;

    private TuiClientRunner(IApplication app, CancellationTokenSource stop, Task completion)
    {
        _app = app;
        _stop = stop;
        Completion = completion;
    }

    /// <summary>
    /// Completes when the client exited, faults if it crashed.
    /// </summary>
    public Task Completion { get; }

    public static async Task<TuiClientRunner> StartAsync(LoginOptions options, ClientSettings? settings = null)
    {
        // otherwise the driver would take over the terminal the tests run in, if there is one
        Environment.SetEnvironmentVariable("DisableRealDriverIO", "1");

        TaskCompletionSource<IApplication> started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenSource stop = new();
        Task completion = Task.Run(async () =>
        {
            try
            {
                using IApplication app = Application.Create().Init(DriverRegistry.Names.ANSI);
                app.Driver!.SetScreenSize(120, 30);
                started.SetResult(app);
                // runs the main loop on this thread until the client exits, like Program.cs does
                await TuiClient.RunAsync(app, options, settings, stop.Token);
            }
            catch (Exception e)
            {
                started.TrySetException(e);
                throw;
            }
        });

        IApplication app = await started.Task.WaitAsync(Timeout);
        return new TuiClientRunner(app, stop, completion);
    }

    /// <summary>
    /// Presses <paramref name="key"/>, the key is handled on the main loop like a real key press.
    /// </summary>
    public Task PressAsync(Key key) => OnMainLoopAsync(() => _app.InjectKey(key));

    /// <summary>
    /// Double clicks the left mouse button at a position on the screen.
    /// </summary>
    public Task DoubleClickAsync(int x, int y) =>
        OnMainLoopAsync(() =>
            _app.InjectSequence(InputInjectionExtensions.LeftButtonDoubleClick(new System.Drawing.Point(x, y)))
        );

    /// <summary>
    /// Types <paramref name="text"/> key by key.
    /// </summary>
    public async Task TypeAsync(string text)
    {
        foreach (char c in text)
        {
            await PressAsync(new Key(c));
        }
    }

    /// <summary>
    /// Types a <c>:</c> command and presses Enter, from normal mode.
    /// </summary>
    public async Task CommandAsync(string command)
    {
        await PressAsync(new Key(':'));
        await TypeAsync(command);
        await PressAsync(Key.Enter);
    }

    /// <summary>
    /// The text currently shown on the screen.
    /// </summary>
    public Task<string> GetScreenAsync() => OnMainLoopAsync(() => _app.Driver?.ToString() ?? "");

    /// <summary>
    /// Waits until the screen shows <paramref name="text"/>, fails with the last screen after
    /// <see cref="Poll.DefaultTimeout"/>.
    /// </summary>
    public async Task WaitForTextAsync(string text)
    {
        string screen = "";
        try
        {
            await Poll.UntilAsync(
                async () =>
                {
                    if (Completion.IsCompleted)
                    {
                        throw new InvalidOperationException(
                            $"The client exited while waiting for \"{text}\".",
                            Completion.Exception
                        );
                    }

                    screen = await GetScreenAsync();
                    return screen.Contains(text, StringComparison.Ordinal);
                },
                $"\"{text}\" to show up"
            );
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException($"{e.Message} The screen was:\n{screen}", e);
        }
    }

    /// <summary>
    /// Waits until a part of the screen matches <paramref name="pattern"/>.
    /// </summary>
    public async Task WaitForMatchAsync(string pattern)
    {
        System.Text.RegularExpressions.Regex regex = new(
            pattern,
            System.Text.RegularExpressions.RegexOptions.None,
            TimeSpan.FromSeconds(1)
        );
        string screen = "";
        try
        {
            await Poll.UntilAsync(
                async () =>
                {
                    screen = await GetScreenAsync();
                    return regex.IsMatch(screen);
                },
                $"/{pattern}/ to show up"
            );
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException($"{e.Message} The screen was:\n{screen}", e);
        }
    }

    /// <summary>
    /// Waits until the screen no longer shows <paramref name="text"/>.
    /// </summary>
    public async Task WaitForTextGoneAsync(string text)
    {
        string screen = "";
        try
        {
            await Poll.UntilAsync(
                async () =>
                {
                    screen = await GetScreenAsync();
                    return !screen.Contains(text, StringComparison.Ordinal);
                },
                $"\"{text}\" to disappear"
            );
        }
        catch (TimeoutException e)
        {
            throw new TimeoutException($"{e.Message} The screen was:\n{screen}", e);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            // the stop token is cancelled already, only the timeout limits waiting for the client to exit
            await Completion.WaitAsync(Timeout, CancellationToken.None);
        }
        finally
        {
            _stop.Dispose();
        }
    }

    private async Task<T> OnMainLoopAsync<T>(Func<T> action)
    {
        TaskCompletionSource<T> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _app.Invoke(() =>
        {
            try
            {
                result.SetResult(action());
            }
            catch (Exception e)
            {
                result.SetException(e);
            }
        });
        return await result.Task.WaitAsync(Timeout, _stop.Token);
    }

    private Task OnMainLoopAsync(Action action) =>
        OnMainLoopAsync(() =>
        {
            action();
            return true;
        });
}

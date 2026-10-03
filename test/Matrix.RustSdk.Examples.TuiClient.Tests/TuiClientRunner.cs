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
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

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

    public static async Task<TuiClientRunner> StartAsync(LoginOptions options)
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
                await TuiClient.RunAsync(app, options, stop.Token);
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
    /// The text currently shown on the screen.
    /// </summary>
    public Task<string> GetScreenAsync() => OnMainLoopAsync(() => _app.Driver?.ToString() ?? "");

    /// <summary>
    /// Waits until the screen shows <paramref name="text"/>, fails with the last screen after
    /// <see cref="Timeout"/>.
    /// </summary>
    public async Task WaitForTextAsync(string text)
    {
        using CancellationTokenSource timeout = new(Timeout);
        string screen = "";
        while (!timeout.IsCancellationRequested)
        {
            if (Completion.IsCompleted)
            {
                throw new InvalidOperationException(
                    $"The client exited while waiting for \"{text}\".",
                    Completion.Exception
                );
            }

            screen = await GetScreenAsync();
            if (screen.Contains(text, StringComparison.Ordinal))
            {
                return;
            }
            await Task.Delay(PollInterval, CancellationToken.None);
        }
        throw new TimeoutException($"\"{text}\" didn't show up within {Timeout}, the screen was:\n{screen}");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        try
        {
            await Completion.WaitAsync(Timeout);
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
        return await result.Task.WaitAsync(Timeout);
    }

    private Task OnMainLoopAsync(Action action) =>
        OnMainLoopAsync(() =>
        {
            action();
            return true;
        });
}

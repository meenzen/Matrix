using System.Runtime.ExceptionServices;

namespace Matrix.RustSdk;

/// <summary>
/// Passes the progress of an SDK call to an <see cref="IProgress{T}"/>. The progress listeners of the bindings call
/// <see cref="Report"/> on a thread of the SDK, the helper calls <see cref="RunAsync{TResult}"/> with the SDK call.
/// </summary>
/// <remarks>
/// Exceptions must not escape into the SDK, they would become a panic of the task reporting the progress. The first
/// exception of <see cref="IProgress{T}.Report"/> ends the reporting and is thrown once the SDK call finished, the call
/// itself isn't aborted. The SDK keeps reporting after its call completed (upload progress) or while it returns, the
/// gate makes sure no report runs after <see cref="RunAsync{TResult}"/> completed. Completing waits for a running
/// report asynchronously: the call can complete on the thread that awaits it, a UI thread a blocking report waits for.
/// </remarks>
internal sealed class ProgressReporter<T>(IProgress<T>? progress)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _completed;
    private ExceptionDispatchInfo? _exception;

    /// <summary>
    /// Reports <paramref name="value"/> unless the call completed or reporting failed before. Never throws.
    /// </summary>
    public void Report(T value)
    {
        if (progress is null)
        {
            return;
        }
        _gate.Wait();
        try
        {
            if (_completed || _exception is not null)
            {
                return;
            }
            progress.Report(value);
        }
        // thrown once the call finished, it must not reach the SDK
        catch (Exception e)
        {
            _exception = ExceptionDispatchInfo.Capture(e);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs <paramref name="call"/> and stops reporting once it finished. Throws the exception of the call, otherwise
    /// the exception <see cref="IProgress{T}.Report"/> threw.
    /// </summary>
    public async Task<TResult> RunAsync<TResult>(Func<Task<TResult>> call)
    {
        TResult result;
        try
        {
            result = await call().ConfigureAwait(false);
        }
        finally
        {
            await CompleteAsync().ConfigureAwait(false);
        }
        _exception?.Throw();
        return result;
    }

    /// <inheritdoc cref="RunAsync{TResult}"/>
    public async Task RunAsync(Func<Task> call)
    {
        try
        {
            await call().ConfigureAwait(false);
        }
        finally
        {
            await CompleteAsync().ConfigureAwait(false);
        }
        _exception?.Throw();
    }

    private async Task CompleteAsync()
    {
        // waits for a report that is still running
        await _gate.WaitAsync().ConfigureAwait(false);
        _completed = true;
        _gate.Release();
    }
}

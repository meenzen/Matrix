namespace Matrix.RustSdk.Testing;

/// <summary>
/// Polls for conditions that become true eventually, like a message another client sent arriving, instead of waiting
/// for a fixed time.
/// </summary>
public static class Poll
{
    /// <summary>
    /// The default time to wait for a condition, generous because CI runners can be slow.
    /// </summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Calls <paramref name="probe"/> until it returns a value that isn't null and returns that value, throws a
    /// <see cref="TimeoutException"/> naming <paramref name="description"/> after <paramref name="timeout"/>
    /// (<see cref="DefaultTimeout"/> by default).
    /// </summary>
    public static async Task<T> UntilAsync<T>(Func<Task<T?>> probe, string description, TimeSpan? timeout = null)
        where T : class
    {
        TimeSpan limit = timeout ?? DefaultTimeout;
        using CancellationTokenSource deadline = new(limit);
        while (true)
        {
            if (await probe() is { } result)
            {
                return result;
            }

            try
            {
                await Task.Delay(PollInterval, deadline.Token);
            }
            catch (OperationCanceledException)
            {
                throw new TimeoutException($"Timed out after {limit} waiting for {description}.");
            }
        }
    }

    /// <summary>
    /// Calls <paramref name="condition"/> until it returns true, see <see cref="UntilAsync{T}"/>.
    /// </summary>
    public static async Task UntilAsync(Func<Task<bool>> condition, string description, TimeSpan? timeout = null) =>
        await UntilAsync(async () => await condition() ? new object() : null, description, timeout);
}

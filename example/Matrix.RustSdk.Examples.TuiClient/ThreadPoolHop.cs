using System.Runtime.CompilerServices;

namespace Matrix.RustSdk.Examples.TuiClient;

/// <summary>
/// Moves an async method to the thread pool: <c>await ThreadPoolHop.Yield();</c> continues on a thread pool thread
/// without a <see cref="SynchronizationContext"/>.
/// </summary>
/// <remarks>
/// Every async method of the session classes starts with it, so the SDK is never called on the main loop of
/// Terminal.Gui. The SDK can complete a future synchronously while another one is polled, on the thread that polls
/// it. If that thread is the main loop, Terminal.Gui runs the continuation it is posted to the main loop right away,
/// which calls into the SDK again from within the first call and deadlocks.
/// </remarks>
public readonly struct ThreadPoolHop : ICriticalNotifyCompletion
{
    public static ThreadPoolHop Yield() => default;

    public ThreadPoolHop GetAwaiter() => this;

    // an instance member, the compiler looks it up on the awaiter
#pragma warning disable S2325
    public bool IsCompleted => SynchronizationContext.Current is null && Thread.CurrentThread.IsThreadPoolThread;
#pragma warning restore S2325

    public void GetResult()
    {
        // nothing to return
    }

    public void OnCompleted(System.Action continuation) =>
        ThreadPool.QueueUserWorkItem(static state => ((System.Action)state!)(), continuation);

    public void UnsafeOnCompleted(System.Action continuation) =>
        ThreadPool.UnsafeQueueUserWorkItem(static state => ((System.Action)state!)(), continuation);
}

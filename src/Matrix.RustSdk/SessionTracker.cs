using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Bindings.Common;

namespace Matrix.RustSdk;

/// <summary>
/// The <see cref="ClientDelegate"/> of a <see cref="StoredClient"/>: stores that the homeserver ended the session, so
/// the next start doesn't restore a token that doesn't work anymore, cancels <see cref="SessionEnded"/> and passes the
/// calls on to the delegate of the app.
/// </summary>
internal sealed class SessionTracker(DataDirectory directory, ClientDelegate? inner) : ClientDelegate, IDisposable
{
    private readonly Lock _lock = new();
#pragma warning disable S2930 // not disposed: the app may still use the token, the source has no timer to release
    private readonly CancellationTokenSource _sessionEnded = new();
#pragma warning restore S2930

    // the stored session while the client owns the directory, null before the login or restore started and after the
    // client was closed
    private SessionFile? _session;
    private bool _disposed;

    public CancellationToken SessionEnded => _sessionEnded.Token;

    public void Track(SessionFile session)
    {
        lock (_lock)
        {
            if (!_disposed)
            {
                _session = session;
            }
        }
    }

    /// <summary>
    /// Stops changing <c>session.json</c>, the caller is about to change it or release the directory.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _session = null;
            _disposed = true;
        }
    }

    public void DidReceiveAuthError(bool isSoftLogout)
    {
        bool ended = false;
        lock (_lock)
        {
            if (_session is { State: SessionState.Active })
            {
                ended = true;
                // after a soft logout a login with the same device continues, otherwise the device is gone
                _session = _session with
                {
                    State = isSoftLogout ? SessionState.SoftLoggedOut : SessionState.LoggedOut,
                };
                try
                {
                    directory.WriteSession(_session);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // exceptions must not reach the SDK. The next start restores the old session, its first request
                    // fails the same way and calls this method again
                }
            }
        }
        if (ended)
        {
            // the callbacks of the token are the app's code, they don't run on the thread of the SDK
            _ = Task.Run(_sessionEnded.Cancel, CancellationToken.None);
        }

        try
        {
            inner?.DidReceiveAuthError(isSoftLogout);
        }
#pragma warning disable S2221, RCS1075 // exceptions of the app's delegate would become a panic in the SDK
        catch (Exception)
        {
            // ignored, see above
        }
#pragma warning restore S2221, RCS1075
    }

    public void OnBackgroundTaskErrorReport(string taskName, BackgroundTaskFailureReason error)
    {
        try
        {
            inner?.OnBackgroundTaskErrorReport(taskName, error);
        }
#pragma warning disable S2221, RCS1075 // exceptions of the app's delegate would become a panic in the SDK
        catch (Exception)
        {
            // ignored, see above
        }
#pragma warning restore S2221, RCS1075
    }
}

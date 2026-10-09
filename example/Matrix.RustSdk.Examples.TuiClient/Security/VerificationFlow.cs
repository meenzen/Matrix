using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk.Examples.TuiClient.Security;

public enum VerificationStep
{
    /// <summary>No verification is going on.</summary>
    Idle,

    /// <summary>This session asked another one (or user) to verify, waiting for them to accept.</summary>
    Requested,

    /// <summary>Another session (or user) asks to verify, waiting for the user to accept.</summary>
    Incoming,

    /// <summary>The request was accepted, waiting for the emoji comparison to start.</summary>
    Accepted,

    /// <summary>The emojis are shown, waiting for the user to confirm that they match.</summary>
    Comparing,

    /// <summary>The user confirmed, waiting for the other side.</summary>
    Confirmed,

    Done,
    Cancelled,
    Failed,
}

/// <summary>
/// A managed snapshot of the verification, see <see cref="VerificationFlow"/>.
/// </summary>
public sealed record VerificationStatus(VerificationStep Step)
{
    /// <summary>
    /// Who is verified with: the other session or the other user, for messages like "Verify with X".
    /// </summary>
    public string? Partner { get; init; }

    public IReadOnlyList<(string Symbol, string Description)> Emojis { get; init; } = [];

    /// <summary>
    /// The numbers to compare if the other side doesn't support emojis.
    /// </summary>
    public IReadOnlyList<ushort> Decimals { get; init; } = [];

    public bool IsActive =>
        Step
            is not (
                VerificationStep.Idle
                or VerificationStep.Done
                or VerificationStep.Cancelled
                or VerificationStep.Failed
            );

    public static readonly VerificationStatus Idle = new(VerificationStep.Idle);
}

/// <summary>
/// Interactive verification with emojis (SAS) through the <see cref="SessionVerificationController"/> of the SDK:
/// verifying this session with another session of the user, or verifying another user. Requests from other sessions
/// only arrive once the controller exists, <see cref="StartAsync"/> creates it as soon as the cross-signing identity
/// of the user exists. Changes are reported with <see cref="Changed"/> on threads of the SDK.
/// </summary>
public sealed class VerificationFlow(Client client) : SessionVerificationControllerDelegate, IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(3);

    private readonly Lock _lock = new();
    private SessionVerificationController? _controller;
    private bool _isInitiator;
    private SessionVerificationRequestDetails? _incoming;
    private bool _disposed;

    public VerificationStatus Status { get; private set; } = VerificationStatus.Idle;

    public bool IsReady => _controller is not null;

    public event EventHandler? Changed;

    /// <summary>
    /// Creates the controller. Right after the first login the cross-signing identity may not be uploaded yet and the
    /// controller can't be created, it is retried until <paramref name="cancellationToken"/> is cancelled.
    /// </summary>
    internal async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            using Encryption encryption = client.Encryption();
            await encryption.WaitForE2eeInitializationTasks();
            while (true)
            {
                try
                {
                    SessionVerificationController controller = await client.GetSessionVerificationController();
                    lock (_lock)
                    {
                        if (_disposed)
                        {
                            controller.Dispose();
                            return;
                        }
                        controller.SetDelegate(this);
                        _controller = controller;
                    }
                    Changed?.Invoke(this, EventArgs.Empty);
                    return;
                }
                catch (ClientException)
                {
                    // no cross-signing identity yet
                    await Task.Delay(RetryDelay, cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // the session is disposed
        }
    }

    /// <summary>
    /// Asks the other sessions of the user to verify this one.
    /// </summary>
    public async Task RequestDeviceVerificationAsync()
    {
        SessionVerificationController controller = Controller;
        _isInitiator = true;
        Update(new VerificationStatus(VerificationStep.Requested) { Partner = "another session" });
        try
        {
            await controller.RequestDeviceVerification();
        }
        catch
        {
            Update(VerificationStatus.Idle);
            throw;
        }
    }

    /// <summary>
    /// Asks another user to verify each other.
    /// </summary>
    public async Task RequestUserVerificationAsync(string userId)
    {
        SessionVerificationController controller = Controller;
        _isInitiator = true;
        Update(new VerificationStatus(VerificationStep.Requested) { Partner = userId });
        try
        {
            await controller.RequestUserVerification(userId);
        }
        catch
        {
            Update(VerificationStatus.Idle);
            throw;
        }
    }

    /// <summary>
    /// Accepts the incoming request, the other side starts the emoji comparison.
    /// </summary>
    public async Task AcceptAsync()
    {
        SessionVerificationController controller = Controller;
        SessionVerificationRequestDetails details =
            _incoming ?? throw new InvalidOperationException("There is no verification request to accept.");
        _isInitiator = false;
        await controller.AcknowledgeVerificationRequest(details.SenderProfile.UserId, details.FlowId);
        await controller.AcceptVerificationRequest();
    }

    /// <summary>
    /// Confirms that the emojis match.
    /// </summary>
    public async Task ConfirmAsync()
    {
        await Controller.ApproveVerification();
        Update(Status with { Step = VerificationStep.Confirmed });
    }

    /// <summary>
    /// Tells the other side that the emojis don't match, which cancels the verification.
    /// </summary>
    public Task MismatchAsync() => Controller.DeclineVerification();

    public async Task CancelAsync()
    {
        if (Status.Step == VerificationStep.Incoming)
        {
            // not acknowledged yet, there is nothing to cancel in the SDK
            _incoming = null;
            Update(new VerificationStatus(VerificationStep.Cancelled) { Partner = Status.Partner });
            return;
        }
        await Controller.CancelVerification();
    }

    /// <summary>
    /// Forgets a finished verification, so the next one starts from scratch.
    /// </summary>
    public void Dismiss()
    {
        if (!Status.IsActive)
        {
            Update(VerificationStatus.Idle);
        }
    }

    public void DidReceiveVerificationRequest(SessionVerificationRequestDetails details) =>
        Callback(() =>
        {
            _incoming = details;
            string device = details.DeviceDisplayName is { Length: > 0 } name
                ? $"{name} ({details.DeviceId})"
                : details.DeviceId;
            string user = details.SenderProfile.DisplayName ?? details.SenderProfile.UserId;
            Update(new VerificationStatus(VerificationStep.Incoming) { Partner = $"{user}, {device}" });
        });

    public void DidAcceptVerificationRequest() =>
        Callback(() =>
        {
            Update(Status with { Step = VerificationStep.Accepted });
            if (_isInitiator)
            {
                // the side that asked starts the emoji comparison
                _ = StartSasAsync();
            }
        });

    public void DidStartSasVerification() => Callback(() => Update(Status with { Step = VerificationStep.Accepted }));

    public void DidReceiveVerificationData(SessionVerificationData data) =>
        Callback(() =>
        {
            using (data)
            {
                VerificationStatus status = data switch
                {
                    SessionVerificationData.Emojis emojis => Status with
                    {
                        Step = VerificationStep.Comparing,
                        Emojis = [.. emojis.EmojisValue.Select(e => (e.Symbol(), e.Description()))],
                    },
                    SessionVerificationData.Decimals decimals => Status with
                    {
                        Step = VerificationStep.Comparing,
                        Decimals = decimals.Values,
                    },
                    _ => Status,
                };
                Update(status);
            }
        });

    public void DidFail() => Callback(() => Finish(VerificationStep.Failed));

    public void DidCancel() => Callback(() => Finish(VerificationStep.Cancelled));

    public void DidFinish() => Callback(() => Finish(VerificationStep.Done));

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _controller?.SetDelegate(null);
            _controller?.Dispose();
            _controller = null;
        }
    }

    private SessionVerificationController Controller =>
        _controller
        ?? throw new InvalidOperationException(
            "Verification isn't available yet, the encryption of this session is still being set up."
        );

    private async Task StartSasAsync()
    {
        try
        {
            await Controller.StartSasVerification();
        }
        catch (Exception e) when (e is ClientException or InvalidOperationException)
        {
            Finish(VerificationStep.Failed);
        }
    }

    private void Finish(VerificationStep step)
    {
        _incoming = null;
        Update(new VerificationStatus(step) { Partner = Status.Partner });
    }

    private void Update(VerificationStatus status)
    {
        Status = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Runs a callback of the SDK, which must never throw: an exception would become a Rust panic.
    /// </summary>
    private static void Callback(System.Action action)
    {
        try
        {
            action();
        }
#pragma warning disable CA1031 // the state is lost, but the SDK keeps running
        catch (Exception)
#pragma warning restore CA1031
        {
            // ignored
        }
    }
}

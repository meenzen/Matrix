using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Examples.TuiClient.Tests.Support;
using Matrix.RustSdk.Testing;
using Terminal.Gui.Input;

namespace Matrix.RustSdk.Examples.TuiClient.Tests;

/// <summary>
/// Encryption in the TUI client: verifying sessions with emojis and recovering the keys with the recovery key.
/// </summary>
[Category(Homeserver.Category)]
[ClassDataSource<Homeserver>(Shared = SharedType.PerTestSession)]
public partial class EncryptionTests(Homeserver homeserver)
{
    [GeneratedRegex(@"Es[A-Za-z0-9]{2}( [A-Za-z0-9]{4}){11}")]
    private static partial Regex RecoveryKey();

    [Test]
    public async Task Login_ShouldSetUpCrossSigningAndTheBackup()
    {
        // Act
        await using TuiTester tui = await TuiTester.StartAsync(homeserver);

        // Assert: the first session of an account creates the identity and the backup, so it is verified
        await tui.WaitForTextAsync("verified backup:on");
    }

    [Test]
    public async Task IncomingVerification_ShouldVerifyTheOtherSession()
    {
        // Arrange: the TUI client is the verified session, a new session of the same user asks it to verify
        await using TuiTester tui = await TuiTester.StartAsync(homeserver);
        await tui.WaitForTextAsync("verified backup:on");
        Client device = await homeserver.LoginAsync(tui.User);
        await using SyncingDevice syncing = await SyncingDevice.StartAsync(device);
        using Encryption encryption = device.Encryption();
        await encryption.WaitForE2eeInitializationTasks();
        // the new session needs the identity of the user, which it downloads after the login
        using SessionVerificationController controller = await Poll.UntilAsync(
            async () =>
            {
                try
                {
                    return await device.GetSessionVerificationController();
                }
                catch (ClientException)
                {
                    return null;
                }
            },
            "the verification controller"
        );
        RecordingVerificationDelegate recorder = new();
        controller.SetDelegate(recorder);

        await WaitUntilVerificationIsReadyAsync(tui);

        // Act: the device asks, the TUI client accepts
        await controller.RequestDeviceVerification();
        await tui.WaitForTextAsync("wants to verify. Accept? (y/n)");
        await tui.PressAsync('y');

        // the device asked, so it starts the emoji comparison
        await recorder.WaitForAsync("accepted");
        await controller.StartSasVerification();
        string[] emojis = await recorder.EmojisAsync();

        // Assert: both show the same emojis
        await tui.WaitForTextAsync("Do the emojis match? (y/n)");
        string screen = await tui.GetScreenAsync();
        foreach (string emoji in emojis)
        {
            await Assert.That(screen).Contains(emoji);
        }

        // Act: both confirm
        await controller.ApproveVerification();
        await tui.PressAsync('y');

        // Assert
        await tui.WaitForTextAsync("Verified with");
        await recorder.WaitForAsync("finished");
        await Poll.UntilAsync(
            () => Task.FromResult(encryption.VerificationState() == VerificationState.Verified),
            "the device to be verified"
        );
    }

    [Test]
    public async Task OutgoingVerification_ShouldVerifyWithTheOtherSession()
    {
        // Arrange: a new session of the same user waits for requests
        await using TuiTester tui = await TuiTester.StartAsync(homeserver);
        await tui.WaitForTextAsync("verified backup:on");
        Client device = await homeserver.LoginAsync(tui.User);
        await using SyncingDevice syncing = await SyncingDevice.StartAsync(device);
        using Encryption encryption = device.Encryption();
        await encryption.WaitForE2eeInitializationTasks();
        using SessionVerificationController controller = await GetControllerAsync(device);
        RecordingVerificationDelegate recorder = new();
        controller.SetDelegate(recorder);

        // Act: the TUI client asks, the device accepts
        await WaitUntilVerificationIsReadyAsync(tui);
        await tui.CommandAsync("verify");
        await tui.WaitForTextAsync("Waiting for another session to accept");
        SessionVerificationRequestDetails request = await recorder.RequestAsync();
        await controller.AcknowledgeVerificationRequest(request.SenderProfile.UserId, request.FlowId);
        await controller.AcceptVerificationRequest();

        // Assert: the TUI client starts the emoji comparison
        string[] emojis = await recorder.EmojisAsync();
        await tui.WaitForTextAsync("Do the emojis match? (y/n)");
        string screen = await tui.GetScreenAsync();
        foreach (string emoji in emojis)
        {
            await Assert.That(screen).Contains(emoji);
        }

        // Act
        await tui.PressAsync('y');
        await controller.ApproveVerification();

        // Assert
        await tui.WaitForTextAsync("Verified with another session.");
        await recorder.WaitForAsync("finished");
    }

    [Test]
    public async Task ResetRecoveryKey_ShouldReplaceTheKey()
    {
        // Arrange
        await using TuiTester tui = await TuiTester.StartAsync(homeserver);
        await tui.WaitForTextAsync("verified backup:on");
        await tui.CommandAsync("recovery enable");
        await tui.WaitForTextAsync("recovery:on");
        string first = RecoveryKey().Match(await tui.GetScreenAsync()).Value;

        // Act
        await tui.CommandAsync("recovery reset");
        await tui.WaitForTextAsync("Replace the recovery key?");
        await tui.PressAsync('y');

        // Assert
        await tui.WaitForTextAsync("Created a new recovery key, store it.");
        string second = RecoveryKey().Match(await tui.GetScreenAsync()).Value;
        await Assert.That(second).IsNotEmpty();
        await Assert.That(second).IsNotEqualTo(first);
    }

    /// <summary>
    /// Waits until the TUI client receives verification requests, earlier ones are lost.
    /// </summary>
    internal static async Task WaitUntilVerificationIsReadyAsync(TuiTester tui)
    {
        await tui.NormalModeAsync();
        await tui.CommandAsync("recovery");
        await tui.WaitForTextAsync("ready, :verify verifies with another session");
        await tui.PressAsync(Key.Esc);
    }

    /// <summary>
    /// The verification controller of a new session, which needs the identity of the user it downloads after the
    /// login.
    /// </summary>
    internal static Task<SessionVerificationController> GetControllerAsync(Client device) =>
        Poll.UntilAsync(
            async () =>
            {
                try
                {
                    return await device.GetSessionVerificationController();
                }
                catch (ClientException)
                {
                    return null;
                }
            },
            "the verification controller"
        );

    [Test]
    public async Task Recovery_ShouldRestoreTheHistoryInANewSession()
    {
        // Arrange: an encrypted room with a message, the keys are in the backup
        TestUser user = await homeserver.CreateUserAsync("recovery");
        string room = $"Secret {Guid.NewGuid().ToString("N")[..6]}";
        string recoveryKey;
        await using (TuiTester first = await TuiTester.StartAsync(homeserver, user))
        {
            await first.WaitForTextAsync("verified backup:on");
            await first.CommandAsync($"create \"{room}\"");
            await first.WaitForTextAsync("-- INSERT --");
            await first.TypeAsync("top secret");
            await first.PressAsync(Key.Enter);
            await first.WaitForTextAsync("top secret");
            await first.WaitForTextGoneAsync("(sending…)");

            // Act: set up recovery
            await first.NormalModeAsync();
            await first.CommandAsync("recovery enable");

            // Assert
            await first.WaitForTextAsync("Your recovery key, store it somewhere safe:");
            await first.WaitForTextAsync("recovery:on");
            recoveryKey = RecoveryKey().Match(await first.GetScreenAsync()).Value;
            await Assert.That(recoveryKey).IsNotEmpty();
            await first.CommandAsync("q");
        }

        // Act: a new session can't read the message until it recovers
        await using TuiTester second = await TuiTester.StartAsync(homeserver, user);
        await second.WaitForTextAsync("unverified");
        await second.WaitForTextAsync(room);
        await second.CommandAsync($"search {room}");
        await second.PressAsync(Key.Enter);
        await second.WaitForTextAsync("(unable to decrypt");
        await second.NormalModeAsync();
        await second.CommandAsync($"recovery {recoveryKey}");

        // Assert: verified, the keys come from the backup
        await second.WaitForTextAsync("Recovered: this session is verified");
        await second.WaitForTextAsync("verified backup:on recovery:on");
        await second.PressAsync(Key.Esc);
        await second.WaitForTextAsync("top secret");
    }

    /// <summary>
    /// Keeps another session syncing, so it receives the verification messages.
    /// </summary>
    internal sealed class SyncingDevice : IAsyncDisposable
    {
        private readonly SyncService _syncService;

        private SyncingDevice(SyncService syncService) => _syncService = syncService;

        public static async Task<SyncingDevice> StartAsync(Client client)
        {
            SyncService syncService = await client.SyncService().Finish();
            await syncService.Start();
            return new SyncingDevice(syncService);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _syncService.Stop().WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                // the homeserver disposes the client
            }
            _syncService.Dispose();
        }
    }

    /// <summary>
    /// Records the callbacks of a verification on the SDK side.
    /// </summary>
    internal sealed class RecordingVerificationDelegate : SessionVerificationControllerDelegate
    {
        private readonly ConcurrentQueue<string> _events = new();
        private volatile string[]? _emojis;

        public IReadOnlyCollection<string> Events => _events;

        private volatile SessionVerificationRequestDetails? _request;

        public void DidReceiveVerificationRequest(SessionVerificationRequestDetails details)
        {
            _request = details;
            _events.Enqueue("request");
        }

        public Task<SessionVerificationRequestDetails> RequestAsync() =>
            Poll.UntilAsync(() => Task.FromResult(_request), "the verification request");

        public void DidAcceptVerificationRequest() => _events.Enqueue("accepted");

        public void DidStartSasVerification() => _events.Enqueue("started");

        public void DidReceiveVerificationData(SessionVerificationData data)
        {
            if (data is SessionVerificationData.Emojis emojis)
            {
                _emojis = [.. emojis.EmojisValue.Select(e => e.Description())];
            }
            _events.Enqueue("data");
        }

        public void DidFail() => _events.Enqueue("failed");

        public void DidCancel() => _events.Enqueue("cancelled");

        public void DidFinish() => _events.Enqueue("finished");

        public Task WaitForAsync(string name) =>
            Poll.UntilAsync(
                () =>
                {
                    if (_events.Contains("failed") || _events.Contains("cancelled"))
                    {
                        throw new InvalidOperationException($"The verification ended: {string.Join(", ", _events)}");
                    }
                    return Task.FromResult(_events.Contains(name));
                },
                $"the verification event {name}"
            );

        public Task<string[]> EmojisAsync() => Poll.UntilAsync(() => Task.FromResult(_emojis), "the emojis");
    }
}

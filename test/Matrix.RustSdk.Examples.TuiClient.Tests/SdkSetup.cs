using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk.Examples.TuiClient.Tests;

/// <summary>
/// Initializes the SDK like <c>Program.cs</c> of the client does, before any test runs: without it the SDK runs on a
/// single threaded runtime, which can't keep up with the clients of the tests running in parallel. The logs end up in
/// the <c>logs</c> directory of the test output.
/// </summary>
public static class SdkSetup
{
    [Before(TestSession)]
    public static void InitializeSdk() =>
        MatrixSdk.Initialize(
            new MatrixSdkOptions
            {
                LogLevel = LogLevel.Info,
                LogDirectory = Path.Combine(AppContext.BaseDirectory, "logs"),
            }
        );
}

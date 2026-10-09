using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk.Tests;

/// <summary>
/// Initializes the SDK like an app does, before any test runs: every test runs with the multi-threaded runtime. The
/// logs end up in the <c>logs</c> directory of the test output.
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

/// <summary>
/// <see cref="MatrixSdk"/>. The SDK can only be initialized once per process, before anything else runs, so
/// <see cref="SdkSetup"/> does it and the tests check what follows.
/// </summary>
public class MatrixSdkTests
{
    [Test]
    public async Task IsInitialized_ShouldBeTrueAfterInitialize()
    {
        // Assert
        await Assert.That(MatrixSdk.IsInitialized).IsTrue();
    }

    [Test]
    public async Task Initialize_ShouldThrowWhenCalledAgain()
    {
        // Act
        static void Initialize() => MatrixSdk.Initialize();

        // Assert
        await Assert.That(Initialize).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task InitPlatform_ShouldThrowAfterInitialize()
    {
        // Arrange
        TracingConfiguration tracing = new(LogLevel.Warn, [], [], WriteToStdoutOrSystem: false, WriteToFiles: null);

        // Act
        void InitPlatform() => MatrixSdkFfiMethods.InitPlatform(tracing, useLightweightTokioRuntime: false);

        // Assert
        // MatrixSdk.Initialize maps it to InvalidOperationException when InitPlatform was called directly
        await Assert.That(InitPlatform).ThrowsExactly<PanicException>();
    }
}

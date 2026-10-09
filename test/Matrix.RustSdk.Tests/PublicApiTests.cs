using Matrix.RustSdk.Bindings;
using Matrix.RustSdk.Testing;
using PublicApiGenerator;

namespace Matrix.RustSdk.Tests;

/// <summary>
/// Snapshots the public API of Matrix.RustSdk, including the generated subscriptions, so changes to it are reviewed.
/// Accept intended changes in <c>Snapshots/PublicApiTests.MatrixRustSdk_ShouldMatchTheSnapshot.verified.txt</c>.
/// </summary>
public class PublicApiTests
{
    [Test]
    public async Task MatrixRustSdk_ShouldMatchTheSnapshot()
    {
        // Arrange
        ApiGeneratorOptions options = new()
        {
            // which assemblies see the internals isn't part of the public API
            ExcludeAttributes = ["System.Runtime.CompilerServices.InternalsVisibleToAttribute"],
        };

        // Act
        string api = typeof(ClientExtensions).Assembly.GeneratePublicApi(options);

        // Assert
        await Snapshot.VerifyAsync(api);
    }
}

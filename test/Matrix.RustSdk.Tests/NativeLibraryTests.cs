using Matrix.RustSdk.Bindings;

namespace Matrix.RustSdk.Tests;

/// <summary>
/// Smoke tests for the native library that don't need a homeserver, so they can run on every platform.
/// The first call into the library also verifies the uniffi contract version and API checksums.
/// </summary>
public class NativeLibraryTests
{
    [Test]
    public async Task SdkGitSha_ShouldNotBeEmpty()
    {
        await Assert.That(MatrixSdkFfiMethods.SdkGitSha()).IsNotNullOrWhiteSpace();
    }

    [Test]
    [Arguments("#room:example.org", true)]
    [Arguments("room:example.org", false)]
    [Arguments("#room", false)]
    public async Task IsRoomAliasFormatValid_ShouldValidateAlias(string alias, bool expected)
    {
        await Assert.That(MatrixSdkFfiMethods.IsRoomAliasFormatValid(alias)).IsEqualTo(expected);
    }

    [Test]
    public async Task MatrixToUserPermalink_ShouldCreatePermalink()
    {
        string permalink = MatrixSdkFfiMethods.MatrixToUserPermalink("@alice:example.org");

        await Assert.That(permalink).IsEqualTo("https://matrix.to/#/@alice:example.org");
    }

    [Test]
    public async Task ServerNameFromUserId_ShouldReturnServerName()
    {
        string serverName = MatrixSdkFfiMethods.ServerNameFromUserId("@alice:example.org");

        await Assert.That(serverName).IsEqualTo("example.org");
    }

    [Test]
    public async Task ClientBuilder_ShouldBuildClient()
    {
        // exercises the async runtime across the FFI boundary, building a client doesn't contact the homeserver
        using Client client = await new ClientBuilder().HomeserverUrl("http://localhost:1").InMemoryStore().Build();

        await Assert.That(client.Homeserver()).StartsWith("http://localhost:1");
    }
}

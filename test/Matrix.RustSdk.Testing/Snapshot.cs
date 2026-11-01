using System.Reflection;
using System.Runtime.CompilerServices;

namespace Matrix.RustSdk.Testing;

/// <summary>
/// Compares a text with <c>Snapshots/&lt;Class&gt;.&lt;Test&gt;.verified.txt</c> in the test project. A changed or
/// missing snapshot fails the test and writes the actual text next to it as <c>.received.txt</c>: review the
/// difference and rename the file to <c>.verified.txt</c>, or run the tests with <c>UPDATE_SNAPSHOTS=1</c> to accept
/// all changes.
/// </summary>
public static class Snapshot
{
    private static readonly string SnapshotDirectory = Path.Join(FindProjectDirectory(), "Snapshots");

    public static async Task VerifyAsync(
        string received,
        [CallerFilePath] string testFile = "",
        [CallerMemberName] string test = ""
    )
    {
        received = received.ReplaceLineEndings("\n");
        string name = $"{Path.GetFileNameWithoutExtension(testFile)}.{test}";
        string verifiedPath = Path.Join(SnapshotDirectory, $"{name}.verified.txt");
        string receivedPath = Path.Join(SnapshotDirectory, $"{name}.received.txt");

        string? verified = File.Exists(verifiedPath)
            ? (await File.ReadAllTextAsync(verifiedPath)).ReplaceLineEndings("\n")
            : null;
        if (received == verified)
        {
            File.Delete(receivedPath);
            return;
        }

        Directory.CreateDirectory(SnapshotDirectory);
        if (Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1")
        {
            await File.WriteAllTextAsync(verifiedPath, received);
            File.Delete(receivedPath);
            return;
        }

        await File.WriteAllTextAsync(receivedPath, received);
        string reason = verified is null ? "doesn't exist yet" : "doesn't match";
        Assert.Fail(
            $"The snapshot {verifiedPath} {reason}. Review {receivedPath} and rename it to .verified.txt, or run the "
                + "tests with UPDATE_SNAPSHOTS=1 to accept all changes."
        );
    }

    /// <summary>
    /// The directory of the test project. CI builds map the source paths to <c>/_/</c>, so
    /// <see cref="CallerFilePathAttribute"/> can't provide it, the test projects store it in an assembly attribute.
    /// </summary>
    private static string FindProjectDirectory()
    {
        // qualified, TUnit has a HookType.Assembly
        System.Reflection.Assembly? tests = System.Reflection.Assembly.GetEntryAssembly();
        return tests
                ?.GetCustomAttributes<AssemblyMetadataAttribute>()
                .SingleOrDefault(metadata => metadata.Key == "ProjectDirectory")
                ?.Value
            ?? throw new InvalidOperationException("The test project has no ProjectDirectory assembly metadata.");
    }
}

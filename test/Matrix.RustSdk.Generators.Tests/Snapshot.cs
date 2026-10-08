using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Matrix.RustSdk.Generators.Tests;

/// <summary>
/// Compares the output of a generator run with <c>Snapshots/&lt;Class&gt;.&lt;Test&gt;.verified.txt</c>. A changed or
/// missing snapshot fails the test and writes the actual output next to it as <c>.received.txt</c>: review the
/// difference and rename the file to <c>.verified.txt</c>, or run the tests with <c>UPDATE_SNAPSHOTS=1</c> to accept
/// all changes.
/// </summary>
internal static class Snapshot
{
    public static async Task VerifyAsync(
        GeneratorRun run,
        [CallerFilePath] string testFile = "",
        [CallerMemberName] string test = ""
    )
    {
        string directory = Path.Combine(Path.GetDirectoryName(testFile)!, "Snapshots");
        string name = $"{Path.GetFileNameWithoutExtension(testFile)}.{test}";
        string verifiedPath = Path.Combine(directory, $"{name}.verified.txt");
        string receivedPath = Path.Combine(directory, $"{name}.received.txt");

        string received = Render(run);
        string? verified = File.Exists(verifiedPath)
            ? (await File.ReadAllTextAsync(verifiedPath)).ReplaceLineEndings("\n")
            : null;
        if (received == verified)
        {
            File.Delete(receivedPath);
            return;
        }

        Directory.CreateDirectory(directory);
        if (Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1")
        {
            await File.WriteAllTextAsync(verifiedPath, received);
            File.Delete(receivedPath);
            return;
        }

        await File.WriteAllTextAsync(receivedPath, received);
        string reason = verified is null ? "doesn't exist yet" : "doesn't match the generated output";
        Assert.Fail(
            $"The snapshot {verifiedPath} {reason}. Review {receivedPath} and rename it to .verified.txt, or run the "
                + "tests with UPDATE_SNAPSHOTS=1 to accept all changes."
        );
    }

    /// <summary>
    /// The generated sources ordered by their hint name, followed by the diagnostics of the generator.
    /// </summary>
    private static string Render(GeneratorRun run)
    {
        GeneratorDriverRunResult result = run.Driver.GetRunResult();
        StringBuilder builder = new();
        foreach (
            GeneratedSourceResult source in result
                .Results.SelectMany(r => r.GeneratedSources)
                .OrderBy(s => s.HintName, StringComparer.Ordinal)
        )
        {
            builder.Append("// ----- ").Append(source.HintName).Append(" -----\n");
            builder.Append(source.SourceText.ToString().ReplaceLineEndings("\n"));
            builder.Append('\n');
        }
        foreach (Diagnostic diagnostic in result.Diagnostics.OrderBy(d => d.Location.SourceSpan.Start))
        {
            FileLinePositionSpan span = diagnostic.Location.GetLineSpan();
            builder
                .Append("// ----- ")
                .Append(diagnostic.Id)
                .Append(' ')
                .Append(diagnostic.Severity)
                .Append(" at ")
                .Append(span.StartLinePosition.Line + 1)
                .Append(':')
                .Append(span.StartLinePosition.Character + 1)
                .Append(" -----\n")
                .Append(diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture))
                .Append('\n');
        }
        return builder.ToString();
    }
}

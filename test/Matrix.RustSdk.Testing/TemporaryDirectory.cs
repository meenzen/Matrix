namespace Matrix.RustSdk.Testing;

/// <summary>
/// A new, empty directory in the temporary directory, deleted with everything in it when disposed.
/// </summary>
public sealed class TemporaryDirectory : IDisposable
{
    private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("matrix-rustsdk-");

    public string Path => _directory.FullName;

    public void Dispose()
    {
        try
        {
            _directory.Delete(recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
            // deleted by the test
        }
    }
}

using TUnit.Core.Interfaces;

namespace Matrix.RustSdk.Testing;

/// <summary>
/// The number of tests a test project runs at the same time, applied to every test project by
/// <c>test/ParallelLimiter.cs</c>. TUnit runs all tests in parallel by default and <c>dotnet test</c> runs the test
/// projects in parallel as well, on a CI runner the homeserver tests then compete for the CPU and time out. The
/// <c>TEST_PARALLEL_LIMIT</c> environment variable overrides it.
/// </summary>
public sealed class TestParallelLimit : IParallelLimit
{
    public int Limit { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("TEST_PARALLEL_LIMIT"), out int limit) && limit > 0
            ? limit
            : Environment.ProcessorCount;
}

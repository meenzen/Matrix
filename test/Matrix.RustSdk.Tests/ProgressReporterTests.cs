using TUnit.Assertions.Enums;

namespace Matrix.RustSdk.Tests;

/// <summary>
/// The runtime part of the progress helpers, without the native library: the tests report like a listener of the SDK.
/// </summary>
public class ProgressReporterTests
{
    [Test]
    public async Task Report_ShouldPassValuesWhileTheCallRuns()
    {
        // Arrange
        List<int> reported = [];
        ProgressReporter<int> reporter = new(new SyncProgress<int>(reported.Add));

        // Act
        string result = await reporter.RunAsync(async () =>
        {
            reporter.Report(1);
            await Task.Yield();
            reporter.Report(2);
            return "done";
        });

        // Assert
        await Assert.That(result).IsEqualTo("done");
        await Assert.That(reported).IsEquivalentTo([1, 2], CollectionOrdering.Matching);
    }

    [Test]
    public async Task Report_ShouldBeIgnoredAfterTheCallCompleted()
    {
        // Arrange
        List<int> reported = [];
        ProgressReporter<int> reporter = new(new SyncProgress<int>(reported.Add));
        await reporter.RunAsync(() => Task.CompletedTask);

        // Act
        reporter.Report(1);

        // Assert
        await Assert.That(reported).IsEmpty();
    }

    [Test]
    public async Task Report_ShouldIgnoreValuesWithoutProgress()
    {
        // Arrange
        ProgressReporter<int> reporter = new(null);

        // Act
        int result = await reporter.RunAsync(() =>
        {
            reporter.Report(1);
            return Task.FromResult(42);
        });

        // Assert
        await Assert.That(result).IsEqualTo(42);
    }

    [Test]
    public async Task RunAsync_ShouldWaitForARunningReport()
    {
        // Arrange
        using SemaphoreSlim reporting = new(0);
        using SemaphoreSlim release = new(0);
        bool reportFinished = false;
        ProgressReporter<int> reporter = new(
            new SyncProgress<int>(_ =>
            {
                reporting.Release();
                release.Wait();
                reportFinished = true;
            })
        );
        // the SDK reports on its own thread, the report can still run when the call returns
        Task report = Task.Run(() => reporter.Report(1));
        await reporting.WaitAsync();

        // Act
        Task run = reporter.RunAsync(() => Task.CompletedTask);
        bool completedDuringReport = await Task.WhenAny(run, Task.Delay(100)) == run;
        release.Release();
        await run;
        await report;

        // Assert
        await Assert.That(completedDuringReport).IsFalse();
        await Assert.That(reportFinished).IsTrue();
    }

    [Test]
    public async Task RunAsync_ShouldThrowTheExceptionOfReportOnceTheCallFinished()
    {
        // Arrange
        List<int> reported = [];
        InvalidOperationException exception = new("report failed");
        ProgressReporter<int> reporter = new(
            new SyncProgress<int>(value =>
            {
                reported.Add(value);
                throw exception;
            })
        );
        bool callFinished = false;

        // Act
        async Task<int> Run() =>
            await reporter.RunAsync(async () =>
            {
                reporter.Report(1);
                // ends the reporting, the call goes on
                reporter.Report(2);
                await Task.Yield();
                callFinished = true;
                return 42;
            });

        // Assert
        await Assert.That(Run).Throws<InvalidOperationException>().WithMessage("report failed");
        await Assert.That(callFinished).IsTrue();
        await Assert.That(reported).IsEquivalentTo([1], CollectionOrdering.Matching);
    }

    [Test]
    public async Task RunAsync_ShouldPreferTheExceptionOfTheCall()
    {
        // Arrange
        ProgressReporter<int> reporter = new(new SyncProgress<int>(_ => throw new InvalidOperationException()));

        // Act
        Task Run() =>
            reporter.RunAsync(() =>
            {
                reporter.Report(1);
                throw new TimeoutException();
            });

        // Assert
        await Assert.That(Run).Throws<TimeoutException>();
    }

    /// <summary>
    /// Reports synchronously, unlike <see cref="Progress{T}"/>, which posts to the synchronization context.
    /// </summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}

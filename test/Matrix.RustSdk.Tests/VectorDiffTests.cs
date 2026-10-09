using TUnit.Assertions.Enums;

namespace Matrix.RustSdk.Tests;

/// <summary>
/// Applying and disposing <see cref="VectorDiff{T}"/>, without the SDK.
/// </summary>
public class VectorDiffTests
{
    public static IEnumerable<Func<(VectorDiff<int> Diff, int[] Expected, int[] Removed)>> Diffs()
    {
        // applied to [1, 2, 3]
        yield return () => (new VectorDiff<int>.Append([4, 5]), [1, 2, 3, 4, 5], []);
        yield return () => (new VectorDiff<int>.Clear(), [], [1, 2, 3]);
        yield return () => (new VectorDiff<int>.PushFront(0), [0, 1, 2, 3], []);
        yield return () => (new VectorDiff<int>.PushBack(4), [1, 2, 3, 4], []);
        yield return () => (new VectorDiff<int>.PopFront(), [2, 3], [1]);
        yield return () => (new VectorDiff<int>.PopBack(), [1, 2], [3]);
        yield return () => (new VectorDiff<int>.Insert(1, 9), [1, 9, 2, 3], []);
        yield return () => (new VectorDiff<int>.Insert(3, 9), [1, 2, 3, 9], []);
        yield return () => (new VectorDiff<int>.Set(1, 9), [1, 9, 3], [2]);
        yield return () => (new VectorDiff<int>.Remove(1), [1, 3], [2]);
        yield return () => (new VectorDiff<int>.Truncate(1), [1], [2, 3]);
        yield return () => (new VectorDiff<int>.Truncate(3), [1, 2, 3], []);
        yield return () => (new VectorDiff<int>.Reset([7, 8]), [7, 8], [1, 2, 3]);
    }

    [Test]
    [MethodDataSource(nameof(Diffs))]
    public async Task ApplyTo_ShouldChangeTheListAndReportRemovedItems(
        VectorDiff<int> diff,
        int[] expected,
        int[] removed
    )
    {
        // Arrange
        List<int> list = [1, 2, 3];
        List<int> reported = [];

        // Act
        diff.ApplyTo(list, reported.Add);

        // Assert
        await Assert.That(list).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(reported).IsEquivalentTo(removed, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ApplyTo_ShouldReportRemovedItemsAfterChangingTheList()
    {
        // Arrange
        List<int> list = [1, 2, 3];
        List<int> countsWhenReported = [];

        // Act
        new VectorDiff<int>.Truncate(1).ApplyTo(list, _ => countsWhenReported.Add(list.Count));

        // Assert
        await Assert.That(countsWhenReported).IsEquivalentTo([1, 1], CollectionOrdering.Matching);
    }

    [Test]
    public async Task ApplyTo_ShouldWorkWithoutRemovedCallback()
    {
        // Arrange
        List<int> list = [1, 2, 3];

        // Act
        new VectorDiff<int>.Reset([4]).ApplyTo(list);

        // Assert
        await Assert.That(list).IsEquivalentTo([4], CollectionOrdering.Matching);
    }

    public static IEnumerable<Func<VectorDiff<int>>> InvalidDiffs()
    {
        // applied to [1, 2, 3]
        yield return () => new VectorDiff<int>.Insert(4, 0);
        yield return () => new VectorDiff<int>.Insert(-1, 0);
        yield return () => new VectorDiff<int>.Set(3, 0);
        yield return () => new VectorDiff<int>.Remove(3);
        yield return () => new VectorDiff<int>.Truncate(4);
        yield return () => new VectorDiff<int>.Truncate(-1);
    }

    [Test]
    [MethodDataSource(nameof(InvalidDiffs))]
    public async Task ApplyTo_ShouldThrowForIndexesOutOfRange(VectorDiff<int> diff)
    {
        // Arrange
        List<int> list = [1, 2, 3];

        // Act & Assert
        await Assert.That(() => diff.ApplyTo(list)).Throws<ArgumentOutOfRangeException>();
        await Assert.That(list).IsEquivalentTo([1, 2, 3], CollectionOrdering.Matching);
    }

    [Test]
    public async Task PopFromEmptyList_ShouldThrow()
    {
        // Act & Assert
        await Assert.That(() => new VectorDiff<int>.PopFront().ApplyTo([])).Throws<InvalidOperationException>();
        await Assert.That(() => new VectorDiff<int>.PopBack().ApplyTo([])).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task Dispose_ShouldDisposeTheValues()
    {
        // Arrange
        Disposable first = new();
        Disposable second = new();
        Disposable third = new();
        VectorDiff<Disposable>[] diffs =
        [
            new VectorDiff<Disposable>.Reset([first, second]),
            new VectorDiff<Disposable>.Set(0, third),
        ];

        // Act
        foreach (VectorDiff<Disposable> diff in diffs)
        {
            diff.Dispose();
        }

        // Assert
        await Assert.That(new[] { first, second, third }.All(d => d.IsDisposed)).IsTrue();
    }

    private sealed class Disposable : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose() => IsDisposed = true;
    }
}

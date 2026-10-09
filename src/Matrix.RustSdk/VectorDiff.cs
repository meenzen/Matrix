using Matrix.RustSdk.Subscriptions;

namespace Matrix.RustSdk;

/// <summary>
/// One change of a list. The SDK describes changes of its lists (timeline items, rooms, threads, search results, ...)
/// with these diffs, applying them in order to a list keeps a copy of it, see <see cref="ApplyTo"/> and
/// <c>ToLiveList</c>.
/// </summary>
/// <remarks>
/// The values belong to the diff until it is applied: disposing a diff disposes the values it contains if they are
/// <see cref="IDisposable"/>, don't dispose diffs whose values you keep.
/// </remarks>
/// <typeparam name="T">The type of the items.</typeparam>
// S3881: only the nested, sealed variants derive from it, there is nothing a Dispose(bool) could be overridden for
#pragma warning disable S3881
public abstract record VectorDiff<T> : IDisposable
#pragma warning restore S3881
{
    // only the nested variants derive from it
    private VectorDiff() { }

    /// <summary>
    /// <see cref="Values"/> were added at the end.
    /// </summary>
    public sealed record Append(IReadOnlyList<T> Values) : VectorDiff<T>
    {
        private protected override void Apply(IList<T> list, Action<T>? removed)
        {
            foreach (T value in Values)
            {
                list.Add(value);
            }
        }

        private protected override IEnumerable<T> GetValues() => Values;
    }

    /// <summary>
    /// All items were removed.
    /// </summary>
    public sealed record Clear : VectorDiff<T>
    {
        private protected override void Apply(IList<T> list, Action<T>? removed) => RemoveFrom(list, 0, removed);
    }

    /// <summary>
    /// <see cref="Value"/> was added at the start.
    /// </summary>
    public sealed record PushFront(T Value) : VectorDiff<T>
    {
        private protected override void Apply(IList<T> list, Action<T>? removed) => list.Insert(0, Value);

        private protected override IEnumerable<T> GetValues() => [Value];
    }

    /// <summary>
    /// <see cref="Value"/> was added at the end.
    /// </summary>
    public sealed record PushBack(T Value) : VectorDiff<T>
    {
        private protected override void Apply(IList<T> list, Action<T>? removed) => list.Add(Value);

        private protected override IEnumerable<T> GetValues() => [Value];
    }

    /// <summary>
    /// The first item was removed.
    /// </summary>
    public sealed record PopFront : VectorDiff<T>
    {
        private protected override void Apply(IList<T> list, Action<T>? removed)
        {
            EnsureNotEmpty(list);
            RemoveAt(list, 0, removed);
        }
    }

    /// <summary>
    /// The last item was removed.
    /// </summary>
    public sealed record PopBack : VectorDiff<T>
    {
        private protected override void Apply(IList<T> list, Action<T>? removed)
        {
            EnsureNotEmpty(list);
            RemoveAt(list, list.Count - 1, removed);
        }
    }

    /// <summary>
    /// <see cref="Value"/> was inserted at <see cref="Index"/>, the items from there on moved back by one.
    /// </summary>
    public sealed record Insert(int Index, T Value) : VectorDiff<T>
    {
        private protected override void Apply(IList<T> list, Action<T>? removed)
        {
            // List<T> allows inserting at Count, other lists may not check it
            ArgumentOutOfRangeException.ThrowIfNegative(Index);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(Index, list.Count);
            list.Insert(Index, Value);
        }

        private protected override IEnumerable<T> GetValues() => [Value];
    }

    /// <summary>
    /// The item at <see cref="Index"/> was replaced with <see cref="Value"/>, for example because it changed.
    /// </summary>
    public sealed record Set(int Index, T Value) : VectorDiff<T>
    {
        private protected override void Apply(IList<T> list, Action<T>? removed)
        {
            T old = list[Index];
            list[Index] = Value;
            removed?.Invoke(old);
        }

        private protected override IEnumerable<T> GetValues() => [Value];
    }

    /// <summary>
    /// The item at <see cref="Index"/> was removed, the items after it moved forward by one.
    /// </summary>
    public sealed record Remove(int Index) : VectorDiff<T>
    {
        private protected override void Apply(IList<T> list, Action<T>? removed) => RemoveAt(list, Index, removed);
    }

    /// <summary>
    /// The items from <see cref="Length"/> on were removed.
    /// </summary>
    public sealed record Truncate(int Length) : VectorDiff<T>
    {
        private protected override void Apply(IList<T> list, Action<T>? removed)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(Length);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(Length, list.Count);
            RemoveFrom(list, Length, removed);
        }
    }

    /// <summary>
    /// All items were replaced with <see cref="Values"/>.
    /// </summary>
    public sealed record Reset(IReadOnlyList<T> Values) : VectorDiff<T>
    {
        private protected override void Apply(IList<T> list, Action<T>? removed)
        {
            RemoveFrom(list, 0, removed);
            foreach (T value in Values)
            {
                list.Add(value);
            }
        }

        private protected override IEnumerable<T> GetValues() => Values;
    }

    /// <summary>
    /// Applies the change to <paramref name="list"/>, which has to contain the items the previous diffs of the same
    /// stream left in it.
    /// </summary>
    /// <param name="list">The list to change.</param>
    /// <param name="removed">
    /// Called with every item that left the list (<see cref="Set"/>, <see cref="Remove"/>, <see cref="PopFront"/>,
    /// <see cref="PopBack"/>, <see cref="Truncate"/>, <see cref="Clear"/>, <see cref="Reset"/>) after the list was
    /// changed, for example to dispose it. The values of the diff move into the list and aren't disposed.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">An index or length is out of the range of the list.</exception>
    /// <exception cref="InvalidOperationException">An item should be removed from an empty list.</exception>
    public void ApplyTo(IList<T> list, Action<T>? removed = null)
    {
        ArgumentNullException.ThrowIfNull(list);
        Apply(list, removed);
    }

    /// <summary>
    /// Disposes the values of the diff that are <see cref="IDisposable"/>.
    /// </summary>
    public void Dispose()
    {
        foreach (T value in GetValues())
        {
            SubscriptionStream.Dispose(value);
        }
        GC.SuppressFinalize(this);
    }

    private protected abstract void Apply(IList<T> list, Action<T>? removed);

    /// <summary>
    /// The values the diff adds to the list.
    /// </summary>
    private protected virtual IEnumerable<T> GetValues() => [];

    private static void EnsureNotEmpty(IList<T> list)
    {
        if (list.Count == 0)
        {
            throw new InvalidOperationException("The list is empty.");
        }
    }

    private static void RemoveAt(IList<T> list, int index, Action<T>? removed)
    {
        T old = list[index];
        list.RemoveAt(index);
        removed?.Invoke(old);
    }

    /// <summary>
    /// Removes the items from <paramref name="start"/> on, from the end so a <see cref="List{T}"/> doesn't move them.
    /// </summary>
    private static void RemoveFrom(IList<T> list, int start, Action<T>? removed)
    {
        T[] old = removed is null ? [] : [.. list.Skip(start)];
        for (int i = list.Count - 1; i >= start; i--)
        {
            list.RemoveAt(i);
        }
        foreach (T item in old)
        {
            removed!(item);
        }
    }
}

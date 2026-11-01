using Matrix.RustSdk.Subscriptions;

namespace Matrix.RustSdk;

/// <summary>
/// Turns the values of the diffs into the items of a <see cref="LiveList{T}"/>. The projection owns the values it gets:
/// it either moves them into the list or disposes them, also when it throws.
/// </summary>
internal abstract class LiveListProjection<TSource, T>
{
    /// <summary>
    /// The item for <paramref name="source"/>.
    /// </summary>
    public abstract ValueTask<T> CreateAsync(TSource source, CancellationToken cancellationToken);

    /// <summary>
    /// Updates <paramref name="item"/> with <paramref name="source"/> in place and returns true, or returns false
    /// without using <paramref name="source"/> if the projection doesn't update items.
    /// </summary>
    public virtual ValueTask<bool> UpdateAsync(T item, TSource source, CancellationToken cancellationToken) =>
        new(false);

    /// <summary>
    /// The items for <paramref name="sources"/>. If one fails, the items created before and the sources after it are
    /// disposed.
    /// </summary>
    // no ConfigureAwait, projections run on the context of the list
#pragma warning disable CA2007
    public virtual async ValueTask<T[]> CreateAllAsync(
        IReadOnlyList<TSource> sources,
        CancellationToken cancellationToken
    )
    {
        T[] items = new T[sources.Count];
        int created = 0;
        try
        {
            for (; created < sources.Count; created++)
            {
                items[created] = await CreateAsync(sources[created], cancellationToken);
            }
            return items;
        }
        catch
        {
            for (int i = 0; i < created; i++)
            {
                SubscriptionStream.Dispose(items[i]);
            }
            // CreateAsync disposed the source it failed for
            for (int i = created + 1; i < sources.Count; i++)
            {
                SubscriptionStream.Dispose(sources[i]);
            }
            throw;
        }
    }
#pragma warning restore CA2007
}

/// <summary>
/// Moves the values into the list.
/// </summary>
internal sealed class OwningProjection<T> : LiveListProjection<T, T>
{
    public static readonly OwningProjection<T> Instance = new();

    public override ValueTask<T> CreateAsync(T source, CancellationToken cancellationToken) => new(source);
}

/// <summary>
/// Projects the values with a selector and disposes them.
/// </summary>
internal sealed class SelectorProjection<TSource, T>(Func<TSource, T> selector, Action<T, TSource>? update)
    : LiveListProjection<TSource, T>
{
    public override ValueTask<T> CreateAsync(TSource source, CancellationToken cancellationToken)
    {
        try
        {
            return new(selector(source));
        }
        finally
        {
            SubscriptionStream.Dispose(source);
        }
    }

    public override ValueTask<bool> UpdateAsync(T item, TSource source, CancellationToken cancellationToken)
    {
        if (update is null)
        {
            return new(false);
        }
        try
        {
            update(item, source);
            return new(true);
        }
        finally
        {
            SubscriptionStream.Dispose(source);
        }
    }
}

/// <summary>
/// Projects the values with an asynchronous selector and disposes them.
/// </summary>
internal sealed class AsyncSelectorProjection<TSource, T>(
    Func<TSource, CancellationToken, ValueTask<T>> selector,
    Func<T, TSource, CancellationToken, ValueTask>? update
) : LiveListProjection<TSource, T>
{
    // no ConfigureAwait, projections run on the context of the list
#pragma warning disable CA2007
    public override async ValueTask<T> CreateAsync(TSource source, CancellationToken cancellationToken)
    {
        try
        {
            return await selector(source, cancellationToken);
        }
        finally
        {
            SubscriptionStream.Dispose(source);
        }
    }

    public override async ValueTask<bool> UpdateAsync(T item, TSource source, CancellationToken cancellationToken)
    {
        if (update is null)
        {
            return false;
        }
        try
        {
            await update(item, source, cancellationToken);
            return true;
        }
        finally
        {
            SubscriptionStream.Dispose(source);
        }
    }

    /// <summary>
    /// Projects all values concurrently, a page of rooms would otherwise wait for one round trip per room. If one
    /// fails, the others are awaited and their items disposed.
    /// </summary>
    public override async ValueTask<T[]> CreateAllAsync(
        IReadOnlyList<TSource> sources,
        CancellationToken cancellationToken
    )
    {
        // every task disposes its source, also when starting it fails
        Task<T>[] tasks = new Task<T>[sources.Count];
        for (int i = 0; i < tasks.Length; i++)
        {
            tasks[i] = CreateAsync(sources[i], cancellationToken).AsTask();
        }
        try
        {
            // in order, the results are on the context of the list then
            T[] items = new T[tasks.Length];
            for (int i = 0; i < tasks.Length; i++)
            {
                items[i] = await tasks[i];
            }
            return items;
        }
        catch
        {
            Task all = Task.WhenAll(tasks);
            await all.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            foreach (Task<T> task in tasks.Where(task => task.IsCompletedSuccessfully))
            {
                SubscriptionStream.Dispose(await task);
            }
            throw;
        }
    }
#pragma warning restore CA2007
}

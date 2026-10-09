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
    public async ValueTask<T[]> CreateAllAsync(IReadOnlyList<TSource> sources, CancellationToken cancellationToken)
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
#pragma warning restore CA2007
}

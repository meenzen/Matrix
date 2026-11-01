namespace Matrix.RustSdk.Subscriptions;

/// <summary>
/// Implements a <c>static partial</c> extension method returning <see cref="IAsyncEnumerable{T}"/> with the
/// subscription method <paramref name="method"/> of the extended type, a method taking a listener and returning a
/// <c>TaskHandle</c>. The source generator in <c>Matrix.RustSdk.Generators</c> writes the listener and the
/// implementation, the parameters between the extended type and the <see cref="CancellationToken"/> are passed to the
/// subscription method by name.
/// </summary>
/// <param name="method">The name of the subscription method, use <c>nameof</c>.</param>
/// <param name="buffer">How values are buffered when the consumer is slower than the SDK.</param>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
internal sealed class SubscriptionAttribute(string method, SubscriptionBuffer buffer) : Attribute
{
    public string Method { get; } = method;

    public SubscriptionBuffer Buffer { get; } = buffer;

    /// <summary>
    /// The name of a method of the extended type returning the current value (or a task of it), for subscriptions that
    /// only deliver changes. It takes the same extra parameters as the subscription method, use <c>nameof</c>. The
    /// current value is yielded first unless the SDK delivered a value in the meantime.
    /// </summary>
    public string? Current { get; init; }

    /// <summary>
    /// Throw an <see cref="InvalidOperationException"/> when the SDK ends the subscription, for subscriptions that only
    /// end because of an error. By default the enumeration ends.
    /// </summary>
    public bool ThrowWhenFinished { get; init; }
}

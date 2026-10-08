namespace Matrix.RustSdk.Subscriptions;

/// <summary>
/// Implements a <c>static partial</c> extension method returning <see cref="IAsyncEnumerable{T}"/> with the
/// subscription method <see cref="Method"/> of the extended type. The source generator writes the listener and the
/// implementation, extra parameters of the declaration are passed to the subscription method by name.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
internal sealed class SubscriptionAttribute(string method) : Attribute
{
    /// <summary>
    /// The name of the subscription method, a method taking a listener and returning a <c>TaskHandle</c>.
    /// </summary>
    public string Method { get; } = method;

    /// <summary>
    /// How values are buffered when the consumer is slower than the SDK.
    /// </summary>
    public SubscriptionBuffer Buffer { get; init; }
}

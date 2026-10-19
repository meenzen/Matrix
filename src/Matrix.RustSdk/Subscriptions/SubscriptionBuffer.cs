namespace Matrix.RustSdk.Subscriptions;

/// <summary>
/// How a subscription buffers values the consumer hasn't read yet.
/// </summary>
internal enum SubscriptionBuffer
{
    /// <summary>
    /// Keeps every value. Required for diffs and events, dropping a diff would corrupt the list it is applied to.
    /// </summary>
    All,

    /// <summary>
    /// Keeps only the latest value, for states where older values are outdated anyway.
    /// </summary>
    Latest,
}

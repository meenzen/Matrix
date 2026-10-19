namespace Matrix.RustSdk.Subscriptions;

/// <summary>
/// Implements a <c>static partial</c> extension method converting a diff enum of the bindings
/// (<c>RoomListEntriesUpdate</c>, ...) to <see cref="VectorDiff{T}"/>. The source generator in
/// <c>Matrix.RustSdk.Generators</c> checks that the enum has exactly the variants of <see cref="VectorDiff{T}"/>.
/// Subscriptions yielding <see cref="VectorDiff{T}"/> arrays convert on their own, this is for hand written listeners.
/// </summary>
[AttributeUsage(AttributeTargets.Method, Inherited = false)]
internal sealed class VectorDiffConversionAttribute : Attribute;

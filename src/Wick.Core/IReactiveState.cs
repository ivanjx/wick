namespace Wick.Core;

/// <summary>
/// Property-level metadata of a reactive object. Implemented by <see cref="ReactiveObject"/> and by
/// the source generator for <see cref="ReactiveStateAttribute"/> types.
/// </summary>
public interface IReactiveState
{
    /// <summary>
    /// Returns the node of the reactive property named <paramref name="propertyName"/>, or <see langword="null"/>
    /// when the property is unknown or has never been read while tracking (nodes are created lazily).
    /// </summary>
    ReactiveNode? FindNode(string propertyName);
}

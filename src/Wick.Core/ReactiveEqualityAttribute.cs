namespace Wick.Core;

/// <summary>
/// Decides when a write to a reactive <c>partial</c> property counts as a change that notifies its readers. Without it,
/// a write of a value equal by <see cref="EqualityComparer{T}.Default"/> is ignored:
/// <code>
/// // A large document: compare references instead of contents on every write.
/// [ReactiveEquality(ReactiveEqualityMode.Reference)]
/// public partial string Content { get; set; } = "";
///
/// [ReactiveEquality(typeof(OrdinalIgnoreCaseComparer))]
/// public partial string Search { get; set; } = "";
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Property, Inherited = false, AllowMultiple = false)]
public sealed class ReactiveEqualityAttribute : Attribute
{
    /// <param name="mode">How a written value is compared with the current one.</param>
    public ReactiveEqualityAttribute(ReactiveEqualityMode mode)
    {
        Mode = mode;
    }

    /// <param name="comparerType">
    /// A non-abstract type with a public parameterless constructor that implements
    /// <see cref="IEqualityComparer{T}"/> for the property's type. One instance is shared by every object of the
    /// declaring type.
    /// </param>
    public ReactiveEqualityAttribute(Type comparerType)
    {
        ComparerType = comparerType;
    }

    /// <summary>The comparison mode; <see cref="ReactiveEqualityMode.Default"/> when a comparer type was given.</summary>
    public ReactiveEqualityMode Mode { get; }

    /// <summary>The comparer type, or <see langword="null"/> when a <see cref="Mode"/> was given.</summary>
    public Type? ComparerType { get; }
}

/// <summary>How a reactive property compares a written value with its current one; see <see cref="ReactiveEqualityAttribute"/>.</summary>
public enum ReactiveEqualityMode
{
    /// <summary><see cref="EqualityComparer{T}.Default"/>, as without the attribute.</summary>
    Default,

    /// <summary>
    /// The same reference: writing another instance notifies even if it is equal, without comparing contents. Only
    /// for reference types.
    /// </summary>
    Reference,

    /// <summary>No comparison: every write notifies, even of the current value.</summary>
    Never,
}

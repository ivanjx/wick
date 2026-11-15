namespace Wick.Core;

/// <summary>
/// Marks a partial class whose <c>partial</c> properties are made reactive by the source generator:
/// <code>
/// [ReactiveState]
/// public partial class AppState
/// {
///     public partial int Count { get; set; }
/// }
/// </code>
/// Reads are tracked by the observer active on the current thread; writes of a different value
/// (by <see cref="EqualityComparer{T}.Default"/>) invalidate the observers of that property only.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ReactiveStateAttribute : Attribute;

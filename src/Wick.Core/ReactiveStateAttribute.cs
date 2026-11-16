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
/// (by <see cref="EqualityComparer{T}.Default"/>, or as <see cref="ReactiveEqualityAttribute"/> decides) invalidate the
/// observers of that property only.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class ReactiveStateAttribute : Attribute
{
    /// <summary>
    /// Whether generated setters also raise <see cref="System.ComponentModel.INotifyPropertyChanged.PropertyChanged"/>
    /// when they notify, so listeners written for <see cref="System.ComponentModel.INotifyPropertyChanged"/> keep working
    /// while a class is migrated. Default <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// The event is raised synchronously by the setter, after the reactive notification, and only when the write
    /// notifies. Types that do not implement <see cref="System.ComponentModel.INotifyPropertyChanged"/> get the
    /// interface, a <c>PropertyChanged</c> event and an <c>OnPropertyChanged(string)</c> method generated; types that
    /// already implement it must have an accessible <c>OnPropertyChanged(string)</c> method, which the setters call.
    /// Derived <see cref="ReactiveStateAttribute"/> types raise the event too.
    /// </remarks>
    public bool NotifyPropertyChanged { get; set; }
}

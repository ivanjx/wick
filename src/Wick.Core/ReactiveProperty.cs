using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Wick.Core;

/// <summary>
/// Accessor bodies emitted by the source generator for <see cref="ReactiveStateAttribute"/> types.
/// Each property owns one lazily created <see cref="ReactiveNode"/> field, so the hot path has no lookup or lock.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static class ReactiveProperty
{
    /// <summary>Records the read (subscribing before the value is read), then returns <paramref name="field"/>.</summary>
    /// <param name="field">The property's backing field.</param>
    /// <param name="node">The property's node field; created on the first tracked read.</param>
    /// <param name="name">The node's diagnostic name, <c>"TypeName.PropertyName"</c>; used only when the node is created.</param>
    public static T Get<T>(ref T field, ref ReactiveNode? node, string name)
    {
        Track(ref node, name);

        return field;
    }

    /// <summary>Writes <paramref name="value"/> and notifies the property's observers when it differs.</summary>
    public static void Set<T>(ref T field, T value, ref ReactiveNode? node)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        Write(ref field, value, ref node);
    }

    /// <summary>Writes <paramref name="value"/> and notifies when it differs by <paramref name="comparer"/>.</summary>
    public static void Set<T>(ref T field, T value, ref ReactiveNode? node, IEqualityComparer<T> comparer)
    {
        if (comparer.Equals(field, value))
        {
            return;
        }

        Write(ref field, value, ref node);
    }

    /// <summary>Writes <paramref name="value"/> and notifies unless it is the instance already held.</summary>
    public static void SetByReference<T>(ref T field, T value, ref ReactiveNode? node)
        where T : class?
    {
        if (ReferenceEquals(field, value))
        {
            return;
        }

        Write(ref field, value, ref node);
    }

    /// <summary>Writes <paramref name="value"/> and notifies, whatever the current value.</summary>
    public static void SetAlways<T>(ref T field, T value, ref ReactiveNode? node) => Write(ref field, value, ref node);

    private static void Write<T>(ref T field, T value, ref ReactiveNode? node)
    {
        ReactiveObserver.ThrowIfTracking();

        field = value;
        Notify(ref node);
    }

    /// <summary>Subscribes the tracking observer to a lazily created node; no allocation when nothing tracks.</summary>
    /// <param name="node">The node field; created on the first tracked read.</param>
    /// <param name="name">A constant diagnostic name for the node, used only when the node is created.</param>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Track(ref ReactiveNode? node, string name)
    {
        var observer = ReactiveObserver.Current;
        if (observer is not null)
        {
            observer.RecordDependency(Volatile.Read(ref node) ?? CreateNode(ref node, name));
        }
    }

    /// <summary>Notifies a lazily created node; call after the guarded state has been written.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Notify(ref ReactiveNode? node)
    {
        // The write must be visible before the node is loaded. Paired with the full fence of the
        // CompareExchange in CreateNode: a reader that creates the node after this load is ordered
        // after the write and therefore reads the new value, so skipping the notification is safe.
        Interlocked.MemoryBarrier();
        Volatile.Read(ref node)?.Notify();
    }

    private static ReactiveNode CreateNode(ref ReactiveNode? node, string name)
    {
        var created = new ReactiveNode(name);

        return Interlocked.CompareExchange(ref node, created, null) ?? created;
    }
}

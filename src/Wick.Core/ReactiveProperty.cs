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
    public static T Get<T>(ref T field, ref ReactiveNode? node)
    {
        Track(ref node);

        return field;
    }

    /// <summary>Writes <paramref name="value"/> and notifies the property's observers when it differs.</summary>
    public static void Set<T>(ref T field, T value, ref ReactiveNode? node)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        ReactiveObserver.ThrowIfTracking();

        field = value;
        Notify(ref node);
    }

    /// <summary>Subscribes the tracking observer to a lazily created node; no allocation when nothing tracks.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Track(ref ReactiveNode? node)
    {
        var observer = ReactiveObserver.Current;
        if (observer is not null)
        {
            observer.RecordDependency(Volatile.Read(ref node) ?? CreateNode(ref node));
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

    private static ReactiveNode CreateNode(ref ReactiveNode? node)
    {
        var created = new ReactiveNode();

        return Interlocked.CompareExchange(ref node, created, null) ?? created;
    }
}

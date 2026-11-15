using System.Runtime.CompilerServices;

namespace Wick.Core;

/// <summary>
/// Runtime (non-generated) base class for reactive state. Properties opt in explicitly:
/// <code>
/// public int Count { get => Get(ref field); set => Set(ref field, value); }
/// </code>
/// Nodes are created lazily, only for properties that were read while tracking. Prefer
/// <see cref="ReactiveStateAttribute"/> with partial properties: generated accessors avoid the
/// per-access name lookup and lock.
/// </summary>
public abstract class ReactiveObject : IReactiveState
{
    private readonly Lock _nodesGate = new();
    private Dictionary<string, ReactiveNode>? _nodes;

    protected T Get<T>(ref T field, [CallerMemberName] string property = "")
    {
        var observer = ReactiveObserver.Current;
        if (observer is not null)
        {
            observer.RecordDependency(GetOrCreateNode(property));
        }

        return field;
    }

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string property = "")
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        ReactiveObserver.ThrowIfTracking();

        field = value;
        FindNode(property)?.Notify();

        return true;
    }

    ReactiveNode? IReactiveState.FindNode(string propertyName) => FindNode(propertyName);

    private ReactiveNode? FindNode(string property)
    {
        lock (_nodesGate)
        {
            return _nodes?.GetValueOrDefault(property);
        }
    }

    private ReactiveNode GetOrCreateNode(string property)
    {
        lock (_nodesGate)
        {
            _nodes ??= [];

            if (!_nodes.TryGetValue(property, out var node))
            {
                node = new ReactiveNode();
                _nodes.Add(property, node);
            }

            return node;
        }
    }
}

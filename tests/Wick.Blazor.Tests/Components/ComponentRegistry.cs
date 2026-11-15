namespace Wick.Blazor.Tests.Components;

/// <summary>
/// Lets tests reach component instances without asking bUnit for them: bUnit keeps strong references
/// to components it has handed out, which would mask leaks in the reactive graph.
/// </summary>
public sealed class ComponentRegistry
{
    private readonly List<WeakReference<ReactiveComponentCore>> _components = [];

    public void Add(ReactiveComponentCore component)
    {
        lock (_components)
        {
            _components.Add(new WeakReference<ReactiveComponentCore>(component));
        }
    }

    public WeakReference<ReactiveComponentCore> Single<T>()
        where T : ReactiveComponentCore
    {
        lock (_components)
        {
            return _components.Single(reference => reference.TryGetTarget(out var target) && target is T);
        }
    }
}

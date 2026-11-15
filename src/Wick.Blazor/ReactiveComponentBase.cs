using Microsoft.AspNetCore.Components.Rendering;

namespace Wick.Blazor;

/// <summary>
/// Base class for components that re-render automatically when reactive state read during their
/// last render changes. Use with <c>@inherits ReactiveComponentBase</c>.
/// </summary>
public abstract class ReactiveComponentBase : ReactiveComponentCore
{
    private protected sealed override void BuildTrackedRenderTree(RenderTreeBuilder builder) => BuildRenderTree(builder);

    /// <summary>Overridden by the Razor compiler; every reactive read made here is tracked.</summary>
    protected new virtual void BuildRenderTree(RenderTreeBuilder builder)
    {
    }
}

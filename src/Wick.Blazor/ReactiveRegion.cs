using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Wick.Blazor;

/// <summary>
/// Tracks reads made while rendering <see cref="ChildContent"/> and re-renders only this region.
/// Needed when reactive content is passed into a non-reactive component (CascadingValue, ErrorBoundary,
/// third-party components): such fragments execute during that component's render, outside any tracking scope.
/// </summary>
public sealed class ReactiveRegion : ReactiveComponentBase
{
    [Parameter]
    public RenderFragment? ChildContent { get; set; }

    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, ChildContent);
}

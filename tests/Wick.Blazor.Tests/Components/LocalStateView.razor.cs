using Microsoft.AspNetCore.Components;
using Wick.Core;

namespace Wick.Blazor.Tests.Components;

/// <summary>Component-local reactive state declared in a code-behind file, where the generator can see it.</summary>
[ReactiveState]
public partial class LocalStateView
{
    // Set by Blazor, so WK0002 does not ask for them to be reactive.
    [Parameter]
    public string Title { get; set; } = "";

    [Inject]
    private RenderProbe Probe { get; set; } = null!;

    public partial int Count { get; set; }
}

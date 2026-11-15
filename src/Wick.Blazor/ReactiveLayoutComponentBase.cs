using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.Components;

namespace Wick.Blazor;

/// <summary>
/// Reactive counterpart of <see cref="LayoutComponentBase"/>: use <c>@inherits ReactiveLayoutComponentBase</c> so
/// that reactive state read by the layout itself (navigation badges, user name) is tracked. <see cref="Body"/>
/// renders the page as its own component, so page reads never re-render the layout.
/// </summary>
public abstract class ReactiveLayoutComponentBase : ReactiveComponentBase
{
    /// <summary>The content to render inside the layout.</summary>
    [Parameter]
    public RenderFragment? Body { get; set; }

    /// <inheritdoc />
    // Layouts are referenced only through LayoutAttribute types, never via OpenComponent<T>, so the trimmer needs
    // an explicit hint to keep the Body property (mirrors LayoutComponentBase).
    [DynamicDependency(DynamicallyAccessedMemberTypes.PublicProperties, typeof(ReactiveLayoutComponentBase))]
    public override Task SetParametersAsync(ParameterView parameters) => base.SetParametersAsync(parameters);
}

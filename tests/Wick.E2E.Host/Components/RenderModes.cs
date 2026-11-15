using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Wick.E2E.Host.Components;

/// <summary>Maps the <c>{Mode}</c> route segment to an interactive render mode; prerendering is off so the first render is interactive.</summary>
public static class RenderModes
{
    public static IComponentRenderMode Get(string mode) => mode switch
    {
        "server" => new InteractiveServerRenderMode(prerender: false),
        "wasm" => new InteractiveWebAssemblyRenderMode(prerender: false),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Expected 'server' or 'wasm'."),
    };
}

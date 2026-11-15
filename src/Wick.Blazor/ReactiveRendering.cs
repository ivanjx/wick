using Microsoft.AspNetCore.Components;
using Wick.Core;

namespace Wick.Blazor;

/// <summary>
/// Waits for reactive re-renders, mainly in component tests. Wick re-renders asynchronously (through the thread pool,
/// then the component's dispatcher), so markup asserted right after a state change may still be stale:
/// <code>
/// var cut = Render&lt;CountDisplay&gt;();
///
/// await Task.Run(() => state.Count = 5);
/// await ReactiveRendering.WhenIdleAsync();
///
/// Assert.Equal("5", cut.Find("#count").TextContent);
/// </code>
/// </summary>
public static class ReactiveRendering
{
    /// <summary>
    /// Completes once no reactive re-render and no <see cref="ReactiveEffect"/> re-run is scheduled or running. Work
    /// that this work starts in turn, such as an effect writing state that another component renders, is awaited as
    /// well; the task completes only when the whole chain has finished.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Process-wide: it also waits for reactive work of other components, including those of tests running in parallel,
    /// which can delay it but never completes it early. Combine it with <see cref="Task.WaitAsync(TimeSpan)"/> for a
    /// timeout.
    /// </para>
    /// <para>
    /// Not covered: asynchronous lifecycle methods, your own <see cref="ComponentBase.InvokeAsync(Action)"/> calls, and
    /// writes deferred by a <see cref="Reactive.BatchAsync"/> that has not completed yet; await those first.
    /// </para>
    /// </remarks>
    public static Task WhenIdleAsync() => PendingWork.WhenIdle();
}

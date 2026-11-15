using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Logging.Abstractions;

namespace Wick.Benchmarks.Rendering;

/// <summary>
/// Minimal renderer with no output: it diffs like any Blazor renderer and counts the components in each batch.
/// A render counts as settled once the dispatcher work item that produced its batch has returned.
/// </summary>
internal sealed class HeadlessRenderer : Renderer
{
    private static readonly TimeSpan s_settleTimeout = TimeSpan.FromSeconds(10);

    private readonly InlineDispatcher _dispatcher;
    private long _batches;
    private long _settledBatches;
    private long _renderedComponents;
    private ExceptionDispatchInfo? _error;

    public HeadlessRenderer()
        : base(EmptyServiceProvider.Instance, NullLoggerFactory.Instance)
    {
        _dispatcher = new InlineDispatcher(OnDispatcherIdle);
    }

    public override Dispatcher Dispatcher => _dispatcher;

    /// <summary>Batches whose dispatcher work item has completed.</summary>
    public long SettledBatches => Volatile.Read(ref _settledBatches);

    /// <summary>Total number of component renders across all batches.</summary>
    public long RenderedComponents => Volatile.Read(ref _renderedComponents);

    public void RenderRoot<TComponent>(IDictionary<string, object?> parameters)
        where TComponent : IComponent
    {
        Dispatcher.InvokeAsync(() =>
        {
            var componentId = AssignRootComponentId(InstantiateComponent(typeof(TComponent)));

            return RenderRootComponentAsync(componentId, ParameterView.FromDictionary(parameters));
        }).GetAwaiter().GetResult();

        _error?.Throw();
    }

    /// <summary>Waits until a batch started after <paramref name="settledBefore"/> was read has settled.</summary>
    public void WaitForRenderAfter(long settledBefore)
    {
        // Spinning without Sleep(1) keeps the wake-up latency out of the measurement; the render runs on the
        // thread pool, which Thread.Yield and Sleep(0) leave room for.
        var spinner = new SpinWait();
        var started = 0L;
        while (SettledBatches == settledBefore)
        {
            _error?.Throw();
            spinner.SpinOnce(sleep1Threshold: -1);

            if (spinner.Count % 4096 == 0)
            {
                if (started == 0)
                {
                    started = Stopwatch.GetTimestamp();
                }
                else if (Stopwatch.GetElapsedTime(started) > s_settleTimeout)
                {
                    throw new TimeoutException("No render settled within the timeout.");
                }
            }
        }

        _error?.Throw();
    }

    protected override void HandleException(Exception exception) => _error ??= ExceptionDispatchInfo.Capture(exception);

    protected override Task UpdateDisplayAsync(in RenderBatch renderBatch)
    {
        Interlocked.Add(ref _renderedComponents, renderBatch.UpdatedComponents.Count);
        Interlocked.Increment(ref _batches);

        return Task.CompletedTask;
    }

    // Runs at the end of the outermost dispatcher work item, still under the dispatcher's lock.
    private void OnDispatcherIdle() => Volatile.Write(ref _settledBatches, Volatile.Read(ref _batches));

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public static readonly EmptyServiceProvider Instance = new();

        public object? GetService(Type serviceType) => null;
    }
}

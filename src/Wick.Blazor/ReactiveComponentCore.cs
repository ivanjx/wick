using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Wick.Core;

namespace Wick.Blazor;

/// <summary>
/// Owns the render-tracking boundary. Razor always emits <c>protected override void BuildRenderTree</c>,
/// so this class seals <see cref="ComponentBase.BuildRenderTree"/> and <see cref="ReactiveComponentBase"/>
/// re-declares a new virtual <c>BuildRenderTree</c> for Razor to override. Only public Blazor API is used.
/// </summary>
public abstract class ReactiveComponentCore : ComponentBase, IDisposable, IAsyncDisposable
{
    private readonly ComponentObserver _observer;
    private int _dirty;
    private int _pendingFlushes;
    private volatile bool _disposed;

    private protected ReactiveComponentCore()
    {
        _observer = new ComponentObserver(this);
    }

    /// <summary>Number of scheduled render flushes that have not completed yet.</summary>
    internal int PendingFlushes => Volatile.Read(ref _pendingFlushes);

    internal int DependencyCount => _observer.DependencyCount;

    protected sealed override void BuildRenderTree(RenderTreeBuilder builder)
    {
        // Clear before reading state: a write racing with this render either is observed by it or
        // re-marks the component dirty and schedules another render.
        Interlocked.Exchange(ref _dirty, 0);

        using (_observer.BeginTracking())
        {
            BuildTrackedRenderTree(builder);
        }
    }

    private protected abstract void BuildTrackedRenderTree(RenderTreeBuilder builder);

    private void OnDependencyChanged()
    {
        if (_disposed || Interlocked.Exchange(ref _dirty, 1) == 1)
        {
            return;
        }

        // Never render inline on the mutating thread: Dispatcher.InvokeAsync runs the work item
        // synchronously when the dispatcher is idle, which would render once per mutation and make
        // the producer pay for rendering. Hop to the thread pool first so consecutive mutations coalesce.
        Interlocked.Increment(ref _pendingFlushes);
        ThreadPool.UnsafeQueueUserWorkItem(static component => component.ScheduleFlush(), this, preferLocal: false);
    }

    // InvokeAsync(Action) reports failures through the returned task; Flush handles its own exceptions.
    private void ScheduleFlush() => _ = InvokeAsync(Flush);

    private void Flush()
    {
        try
        {
            if (!_disposed && Interlocked.Exchange(ref _dirty, 0) == 1)
            {
                StateHasChanged();
            }
        }
        catch (Exception exception)
        {
            _ = DispatchExceptionAsync(exception);
        }
        finally
        {
            Interlocked.Decrement(ref _pendingFlushes);
        }
    }

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Blazor calls only this method on components that implement <see cref="IAsyncDisposable"/>, so it runs
    /// <see cref="DisposeAsyncCore"/> and then <see cref="Dispose(bool)"/>: overrides of either are honored.
    /// </summary>
    public ValueTask DisposeAsync()
    {
        // Stop reacting right away; the asynchronous cleanup may take a while.
        ReleaseSubscriptions();

        ValueTask pending;
        try
        {
            pending = DisposeAsyncCore();
        }
        catch
        {
            Dispose();
            throw;
        }

        if (!pending.IsCompletedSuccessfully)
        {
            return AwaitThenDispose(pending);
        }

        Dispose();

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Override for asynchronous cleanup instead of implementing <see cref="IAsyncDisposable"/> (for example with
    /// <c>@implements IAsyncDisposable</c>), which would bypass the release of reactive subscriptions.
    /// <see cref="Dispose(bool)"/> runs after the returned task completes.
    /// </summary>
    protected virtual ValueTask DisposeAsyncCore() => ValueTask.CompletedTask;

    /// <summary>
    /// Override to release resources; always call the base implementation. Components that
    /// re-implement <see cref="IDisposable"/> or <see cref="IAsyncDisposable"/> (for example via
    /// <c>@implements IDisposable</c>) bypass this method; analyzer WK0007 reports that, and their
    /// subscriptions are only released once the component is garbage collected.
    /// </summary>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposing)
        {
            return;
        }

        ReleaseSubscriptions();
    }

    private void ReleaseSubscriptions()
    {
        _disposed = true;
        _observer.Dispose();
    }

    private async ValueTask AwaitThenDispose(ValueTask pending)
    {
        try
        {
            await pending.ConfigureAwait(false);
        }
        finally
        {
            Dispose();
        }
    }

    /// <summary>
    /// Holds the component weakly so a subscription that outlives a missed <see cref="Dispose()"/>
    /// cannot keep the component alive.
    /// </summary>
    private sealed class ComponentObserver(ReactiveComponentCore component) : ReactiveObserver
    {
        private readonly WeakReference<ReactiveComponentCore> _component = new(component);

        protected override void OnInvalidated()
        {
            if (_component.TryGetTarget(out var target))
            {
                target.OnDependencyChanged();
                return;
            }

            Dispose();
        }
    }
}

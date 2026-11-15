using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Wick.Core;

namespace Wick.Blazor;

/// <summary>
/// Owns the render-tracking boundary. Razor always emits <c>protected override void BuildRenderTree</c>,
/// so this class seals <see cref="ComponentBase.BuildRenderTree"/> and <see cref="ReactiveComponentBase"/>
/// re-declares a new virtual <c>BuildRenderTree</c> for Razor to override. Only public Blazor API is used.
/// </summary>
public abstract class ReactiveComponentCore : ComponentBase, IDisposable, IAsyncDisposable, IReactiveObserverOwner
{
    private static readonly EffectOptions s_defaultEffectOptions = new();

    private readonly ComponentObserver _observer;
    private int _dirty;
    private int _pendingFlushes;
    private volatile bool _disposed;
    private List<IDisposable>? _owned;

    private protected ReactiveComponentCore()
    {
        _observer = new ComponentObserver(this);
    }

    /// <summary>Number of scheduled render flushes that have not completed yet.</summary>
    internal int PendingFlushes => Volatile.Read(ref _pendingFlushes);

    internal int DependencyCount => _observer.DependencyCount;

    ReactiveObserver IReactiveObserverOwner.Observer => _observer;

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

    /// <summary>
    /// Component-owned form of <see cref="Reactive.Effect(Action, EffectOptions?)"/>: the effect is disposed together
    /// with the component, so it needs no cleanup in <see cref="Dispose(bool)"/>. It runs once immediately on the
    /// calling thread and re-runs on the component's dispatcher whenever reactive state it read changes, so UI work
    /// inside it, such as JavaScript interop, needs no <see cref="ComponentBase.InvokeAsync(Action)"/>.
    /// </summary>
    /// <remarks>
    /// The returned effect may be disposed early; disposing it again with the component is harmless. Calling this
    /// method after the component was disposed (for example after an <c>await</c> in <c>OnInitializedAsync</c> that
    /// outlived the component) still runs <paramref name="run"/> once, then disposes the effect immediately so it
    /// never re-runs. Safe to call from any thread.
    /// </remarks>
    /// <param name="run">The side effect; reactive state it reads becomes its dependencies.</param>
    /// <param name="options">
    /// Error handling and timing, see <see cref="EffectOptions"/>. Without <see cref="EffectOptions.Dispatcher"/>, the
    /// component's <see cref="ComponentBase.InvokeAsync(Func{Task})"/> is used.
    /// </param>
    protected ReactiveEffect Effect(Action run, EffectOptions? options = null) => Own(Reactive.Effect(run, OnDispatcher(options)));

    /// <summary>
    /// Component-owned form of <see cref="Reactive.Effect(Func{CancellationToken, Task}, EffectOptions?)"/>: an
    /// asynchronous effect whose re-runs start on the component's dispatcher, so the run and its awaits continue there
    /// and may call JavaScript interop directly:
    /// <code>
    /// Effect(async token => await JS.InvokeVoidAsync("setTheme", token, Settings.Theme));
    /// </code>
    /// Only reads before the first incomplete <c>await</c> are tracked. A change during a run cancels its token and the
    /// next run follows once it completed. Disposing the component cancels the run in progress, unless
    /// <see cref="EffectOptions.FlushOnDispose"/> is set.
    /// </summary>
    /// <remarks>
    /// The first run starts on the calling thread, normally the dispatcher in <c>OnInitialized</c>. Disposal follows
    /// the same rules as the synchronous overload.
    /// </remarks>
    /// <param name="run">The side effect, with a token cancelled when the run is superseded or the component disposed.</param>
    /// <param name="options">
    /// Error handling and timing, see <see cref="EffectOptions"/>; the outcome of a cancelled run is never reported.
    /// Without <see cref="EffectOptions.Dispatcher"/>, the component's <see cref="ComponentBase.InvokeAsync(Func{Task})"/> is used.
    /// </param>
    protected ReactiveEffect Effect(Func<CancellationToken, Task> run, EffectOptions? options = null)
        => Own(Reactive.Effect(run, OnDispatcher(options)));

    /// <summary>
    /// Asynchronous component-owned effect that does not need a cancellation token; see
    /// <see cref="Effect(Func{CancellationToken, Task}, EffectOptions?)"/>.
    /// </summary>
    protected ReactiveEffect Effect(Func<Task> run, EffectOptions? options = null) => Own(Reactive.Effect(run, OnDispatcher(options)));

    /// <summary>
    /// Component-owned form of <see cref="Reactive.Debounced{T}"/>: a value that follows <paramref name="read"/> once
    /// it has stopped changing for <paramref name="delay"/>, disposed together with the component.
    /// </summary>
    protected Debounced<T> Debounced<T>(Func<T> read, TimeSpan delay, TimeProvider? timeProvider = null)
        => Own(Reactive.Debounced(read, delay, timeProvider));

    private EffectOptions OnDispatcher(EffectOptions? options)
    {
        if (options?.Dispatcher is not null)
        {
            return options;
        }

        return (options ?? s_defaultEffectOptions) with { Dispatcher = InvokeAsync };
    }

    private TOwned Own<TOwned>(TOwned owned)
        where TOwned : IDisposable
    {
        // The private observer guards _owned: it never locks itself, and a dedicated Lock would cost every
        // component an allocation even when it owns nothing.
        lock (_observer)
        {
            if (!_disposed)
            {
                (_owned ??= []).Add(owned);

                return owned;
            }
        }

        owned.Dispose();

        return owned;
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
        PendingWork.Add();
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
                if (WickDiagnostics.Enabled)
                {
                    WickDiagnostics.ReportRender(GetType(), _observer.Causes?.Take());
                }

                StateHasChanged();
            }
            else if (WickDiagnostics.Enabled)
            {
                // A render in between (for example the parent's) already covered these changes.
                _observer.Causes?.Clear();
            }
        }
        catch (Exception exception)
        {
            _ = DispatchExceptionAsync(exception);
        }
        finally
        {
            // In this order: once ReactiveRendering.WhenIdleAsync completes, every component reports no pending flush.
            Interlocked.Decrement(ref _pendingFlushes);
            PendingWork.Remove();
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

        List<IDisposable>? owned;
        lock (_observer)
        {
            owned = _owned;
            _owned = null;
        }

        if (owned is null)
        {
            return;
        }

        foreach (var disposable in owned)
        {
            disposable.Dispose();
        }
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

        private ChangeCauses? _causes;

        /// <summary>Changes recorded since the last report; created on the first change seen with diagnostics enabled.</summary>
        internal ChangeCauses? Causes => Volatile.Read(ref _causes);

        internal override void RecordCause(ReactiveNode node)
        {
            var causes = Volatile.Read(ref _causes) ?? CreateCauses();

            causes.Add(node);
        }

        private ChangeCauses CreateCauses()
        {
            var created = new ChangeCauses();

            return Interlocked.CompareExchange(ref _causes, created, null) ?? created;
        }

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

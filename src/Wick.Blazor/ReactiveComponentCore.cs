using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Wick.Core;

namespace Wick.Blazor;

/// <summary>
/// Shared base of <see cref="ReactiveComponentBase"/> and <see cref="ReactiveLayoutComponentBase"/>: tracks the reactive
/// state read while rendering and re-renders the component, on its dispatcher, when that state changes. Derive from one
/// of those two classes; this one cannot be derived from directly.
/// </summary>
/// <remarks>
/// Owns the render-tracking boundary. Razor always emits <c>protected override void BuildRenderTree</c>,
/// so this class seals <see cref="ComponentBase.BuildRenderTree"/> and <see cref="ReactiveComponentBase"/>
/// re-declares a new virtual <c>BuildRenderTree</c> for Razor to override. Only public Blazor API is used.
/// </remarks>
public abstract class ReactiveComponentCore : ComponentBase, IDisposable, IAsyncDisposable, IReactiveObserverOwner
{
    private static readonly EffectOptions s_defaultEffectOptions = new();

    private readonly ComponentObserver _observer;
    private int _dirty;
    private int _pendingFlushes;
    private volatile bool _disposed;
    private List<IDisposable>? _owned;
    private FlushWorkItem? _flushWorkItem;

    // Protected rather than private protected so the public API analyzer tracks the protected members that
    // ReactiveComponentBase exposes (RS0022). Other assemblies still can't derive from this class: they can't
    // implement the private protected BuildTrackedRenderTree.
    /// <summary>Called by the constructors of <see cref="ReactiveComponentBase"/> and <see cref="ReactiveLayoutComponentBase"/>.</summary>
    protected ReactiveComponentCore()
    {
        _observer = new ComponentObserver(this);
    }

    /// <summary>Number of scheduled render flushes that have not completed yet.</summary>
    internal int PendingFlushes => Volatile.Read(ref _pendingFlushes);

    internal int DependencyCount => _observer.DependencyCount;

    ReactiveObserver IReactiveObserverOwner.Observer => _observer;

    /// <summary>Renders the component while tracking the reactive state it reads; override the re-declared <c>BuildRenderTree</c> instead.</summary>
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
    /// Component-owned form of <see cref="Reactive.Watch{T}(Func{T}, Action{T, T}, EffectOptions?, IEqualityComparer{T}?)"/>:
    /// calls <paramref name="onChange"/> with the new and the previous value whenever the value of
    /// <paramref name="source"/> changes, but not for its current value. Calls happen on the component's dispatcher,
    /// and the watch is disposed together with the component:
    /// <code>
    /// Watch(() => Settings.Language, (language, previous) => Relocalize(language));
    /// </code>
    /// </summary>
    /// <param name="source">Computes the watched value; reactive state it reads is tracked. It runs once immediately.</param>
    /// <param name="onChange">Receives the new value, then the previous one; it runs untracked.</param>
    /// <param name="options">
    /// Error handling and timing, see <see cref="EffectOptions"/>. Without <see cref="EffectOptions.Dispatcher"/>, the
    /// component's <see cref="ComponentBase.InvokeAsync(Func{Task})"/> is used.
    /// </param>
    /// <param name="comparer">Decides whether the value changed; <see cref="EqualityComparer{T}.Default"/> by default.</param>
    protected ReactiveEffect Watch<T>(
        Func<T> source,
        Action<T, T> onChange,
        EffectOptions? options = null,
        IEqualityComparer<T>? comparer = null)
        => Own(Reactive.Watch(source, onChange, OnDispatcher(options), comparer));

    /// <summary>
    /// Asynchronous component-owned watch; see
    /// <see cref="Reactive.Watch{T}(Func{T}, Func{T, T, CancellationToken, Task}, EffectOptions?, IEqualityComparer{T}?)"/>.
    /// Calls start on the component's dispatcher; disposing the component cancels the call in progress, unless
    /// <see cref="EffectOptions.FlushOnDispose"/> is set.
    /// </summary>
    protected ReactiveEffect Watch<T>(
        Func<T> source,
        Func<T, T, CancellationToken, Task> onChange,
        EffectOptions? options = null,
        IEqualityComparer<T>? comparer = null)
        => Own(Reactive.Watch(source, onChange, OnDispatcher(options), comparer));

    /// <summary>
    /// Asynchronous component-owned watch that does not need a cancellation token; see
    /// <see cref="Watch{T}(Func{T}, Func{T, T, CancellationToken, Task}, EffectOptions?, IEqualityComparer{T}?)"/>.
    /// </summary>
    protected ReactiveEffect Watch<T>(
        Func<T> source,
        Func<T, T, Task> onChange,
        EffectOptions? options = null,
        IEqualityComparer<T>? comparer = null)
        => Own(Reactive.Watch(source, onChange, OnDispatcher(options), comparer));

    /// <summary>
    /// Component-owned form of
    /// <see cref="Reactive.Watch{TChange}(CollectionChanges{TChange}, Action{IReadOnlyList{TChange}}, EffectOptions?)"/>:
    /// calls <paramref name="onChanges"/> with what changed in a reactive collection, in order, on the component's
    /// dispatcher, until the component is disposed:
    /// <code>
    /// Watch(State.Markers.Changes, changes =&gt; Canvas.Apply(changes));
    /// </code>
    /// </summary>
    /// <param name="changes">The collection's <c>Changes</c>, for example <see cref="ReactiveList{T}.Changes"/>.</param>
    /// <param name="onChanges">Receives every change made since the previous call, never an empty list; it runs untracked.</param>
    /// <param name="options">
    /// Error handling and timing, see <see cref="EffectOptions"/>. Without <see cref="EffectOptions.Dispatcher"/>, the
    /// component's <see cref="ComponentBase.InvokeAsync(Func{Task})"/> is used.
    /// </param>
    protected ReactiveEffect Watch<TChange>(
        CollectionChanges<TChange> changes,
        Action<IReadOnlyList<TChange>> onChanges,
        EffectOptions? options = null)
        => Own(Reactive.Watch(changes, onChanges, OnDispatcher(options)));

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

        // The work item is created on the first re-render, then reused: a component Wick never re-renders pays
        // nothing. Two threads racing here may both create one; either is valid.
        ThreadPool.UnsafeQueueUserWorkItem(_flushWorkItem ??= new FlushWorkItem(this), preferLocal: false);
    }

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

    /// <summary>
    /// Runs <see cref="Dispose(bool)"/>, whose base implementation releases the component's reactive subscriptions and the
    /// effects, watches and debounced values it owns. Blazor calls <see cref="DisposeAsync"/> instead.
    /// </summary>
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
    /// Override to release resources; always call the base implementation. A component that declares its own
    /// <c>Dispose</c> or <c>DisposeAsync</c> (for example via <c>@implements IDisposable</c>) replaces or hides the base
    /// disposal: Blazor then either never calls that declaration or calls it instead of the cleanup that releases reactive
    /// subscriptions, and the state the component read keeps it alive. Analyzer WK0007 reports that.
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
    /// Hands the component's flush to its dispatcher from the thread pool. Stateless apart from the cached delegate, so
    /// one instance serves every flush, even when a render in between lets a second flush be queued before the first ran.
    /// </summary>
    private sealed class FlushWorkItem : IThreadPoolWorkItem
    {
        private readonly ReactiveComponentCore _component;
        private readonly Action _flush;

        public FlushWorkItem(ReactiveComponentCore component)
        {
            _component = component;
            _flush = component.Flush;
        }

        // InvokeAsync(Action) reports failures through the returned task; Flush handles its own exceptions.
        public void Execute() => _ = _component.InvokeAsync(_flush);
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

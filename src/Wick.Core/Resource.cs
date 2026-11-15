namespace Wick.Core;

/// <summary>
/// A value loaded asynchronously, created with <see cref="Reactive.Resource{TSource, T}"/>. The source is tracked like
/// a derived value; when its result changes, the fetch in flight is cancelled and a new one starts with the new result:
/// <code>
/// private readonly Resource&lt;User&gt; _user = Reactive.Resource(() => State.UserId, (id, token) => api.GetUserAsync(id, token));
/// </code>
/// <see cref="Value"/>, <see cref="HasValue"/>, <see cref="IsLoading"/> and <see cref="Error"/> are tracked like
/// reactive properties: a component reading <c>_user.IsLoading</c> re-renders when a fetch starts or settles.
/// </summary>
/// <remarks>
/// Reads made by the fetch are not tracked. The first fetch starts on the creating thread, later ones on the thread
/// that changed the source (at the end of its batch) or called <see cref="Reload"/>, so keep the synchronous part of
/// the fetch short; a fetch requested while that thread tracks dependencies (in a render or an effect) starts on the
/// thread pool instead. It may complete on any thread. A fetch that completes after a newer one started is ignored,
/// whether or not it observed its cancellation. Dispose to cancel the fetch in flight and stop tracking; the state
/// then stays as it was. An unreferenced instance is released after garbage collection, like <see cref="Derived{T}"/>.
/// </remarks>
public sealed class Resource<T> : IDisposable
{
    private const string ValueNodeName = "Resource<T>.Value";
    private const string LoadingNodeName = "Resource<T>.IsLoading";
    private const string ErrorNodeName = "Resource<T>.Error";

    private readonly Lock _gate = new();
    private readonly Func<CancellationToken, Task<T>>? _fetch;
    private SourceWatcher? _watcher;
    private ReactiveNode? _valueNode;
    private ReactiveNode? _loadingNode;
    private ReactiveNode? _errorNode;
    private T? _value;
    private bool _hasValue;
    private bool _isLoading;
    private Exception? _error;
    private CancellationTokenSource? _cancellation;
    private TaskCompletionSource? _settled;
    private bool _disposed;

    // Incremented by every fetch that starts and by every source failure. A fetch applies its outcome only while its
    // version is current, so a superseded fetch cannot overwrite a newer outcome even if it ignores its cancellation.
    private int _version;

    private Resource(Func<CancellationToken, Task<T>>? fetch)
    {
        _fetch = fetch;
    }

    /// <summary>
    /// The result of the last successful fetch, or <c>default</c> before the first one. Kept while a newer fetch runs
    /// and after a failure, so it can stay on screen while the resource reloads.
    /// </summary>
    public T? Value
    {
        get
        {
            ReactiveProperty.Track(ref _valueNode, ValueNodeName);

            lock (_gate)
            {
                return _value;
            }
        }
    }

    /// <summary>True once a fetch has succeeded, so that <see cref="Value"/> holds a result.</summary>
    public bool HasValue
    {
        get
        {
            ReactiveProperty.Track(ref _valueNode, ValueNodeName);

            lock (_gate)
            {
                return _hasValue;
            }
        }
    }

    /// <summary>True while a fetch is in flight.</summary>
    public bool IsLoading
    {
        get
        {
            ReactiveProperty.Track(ref _loadingNode, LoadingNodeName);

            lock (_gate)
            {
                return _isLoading;
            }
        }
    }

    /// <summary>
    /// The exception of the last failed fetch, or of the source; kept while reloading and cleared when a fetch
    /// succeeds. A fetch cancelled because a newer one started is not a failure.
    /// </summary>
    public Exception? Error
    {
        get
        {
            ReactiveProperty.Track(ref _errorNode, ErrorNodeName);

            lock (_gate)
            {
                return _error;
            }
        }
    }

    /// <summary>
    /// Completes when no fetch is in flight: right away while idle, otherwise once the latest fetch settled or the
    /// resource was disposed. Not tracked, and never faults; check <see cref="Error"/> afterwards.
    /// </summary>
    public Task WhenSettled
    {
        get
        {
            lock (_gate)
            {
                if (!_isLoading || _disposed)
                {
                    return Task.CompletedTask;
                }

                return (_settled ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            }
        }
    }

    /// <summary>Fetches again with the current source value, cancelling the fetch in flight. Does nothing after disposal.</summary>
    public void Reload()
    {
        ReactiveObserver.ThrowIfTracking();

        if (_watcher is not null)
        {
            _watcher.Reload();
            return;
        }

        FetchWithoutSource();
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        TaskCompletionSource? settled;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            cancellation = _cancellation;
            settled = _settled;
            _cancellation = null;
            _settled = null;
        }

        _watcher?.Stop();
        Cancel(cancellation);
        settled?.TrySetResult();
    }

    internal static Resource<T> Create(Func<CancellationToken, Task<T>> fetch)
    {
        var resource = new Resource<T>(fetch);
        resource.FetchWithoutSource();

        return resource;
    }

    internal static Resource<T> Create<TSource>(Func<TSource> source, Func<TSource, CancellationToken, Task<T>> fetch)
    {
        var resource = new Resource<T>(fetch: null);
        var watcher = new SourceWatcher<TSource>(resource, source, fetch);
        resource._watcher = watcher;
        watcher.Run();

        return resource;
    }

    private void FetchWithoutSource() => Start(static (fetch, token) => fetch(token), _fetch!);

    /// <summary>Starts a fetch for <paramref name="arg"/>, superseding the one in flight.</summary>
    private void Start<TArg>(Func<TArg, CancellationToken, Task<T>> fetch, TArg arg)
    {
        CancellationTokenSource? superseded;
        CancellationToken token;
        int version;
        bool loadingChanged;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            version = ++_version;
            superseded = _cancellation;
            _cancellation = new CancellationTokenSource();
            token = _cancellation.Token;
            loadingChanged = !_isLoading;
            _isLoading = true;
        }

        Cancel(superseded);

        if (ReactiveObserver.IsTracking)
        {
            // The resource is created or reloaded while dependencies are tracked on this thread (in a render or an
            // effect): the fetch starts on the thread pool so that its reads cannot become dependencies of that observer.
            Publish(valueChanged: false, loadingChanged, errorChanged: false);
            ThreadPool.QueueUserWorkItem(
                static state => state.Owner.Invoke(state.Fetch, state.Arg, state.Token, state.Version, loadingChanged: false),
                (Owner: this, Fetch: fetch, Arg: arg, Token: token, Version: version),
                preferLocal: false);

            return;
        }

        Invoke(fetch, arg, token, version, loadingChanged);
    }

    private void Invoke<TArg>(Func<TArg, CancellationToken, Task<T>> fetch, TArg arg, CancellationToken token, int version, bool loadingChanged)
    {
        Task<T> task;
        try
        {
            task = fetch(arg, token);
        }
        catch (Exception exception)
        {
            task = Task.FromException<T>(exception);
        }

        // A result that is available right away is published in the same notification wave as the loading flag.
        if (task.IsCompleted)
        {
            Settle(task, version, loadingChanged);
            return;
        }

        Publish(valueChanged: false, loadingChanged, errorChanged: false);
        _ = Observe(task, version);
    }

    private async Task Observe(Task<T> fetch, int version)
    {
        await ((Task)fetch).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        Settle(fetch, version, loadingChanged: false);
    }

    /// <summary>
    /// Applies the outcome of a completed fetch if it is still the latest one. <paramref name="loadingChanged"/> is set
    /// when the fetch started the loading state without publishing it yet.
    /// </summary>
    private void Settle(Task<T> fetch, int version, bool loadingChanged)
    {
        T? value = default;
        Exception? error = null;
        try
        {
            value = fetch.GetAwaiter().GetResult();
        }
        catch (Exception exception)
        {
            // Also observes the failure of a superseded fetch, so it never surfaces as an unobserved task exception.
            error = exception;
        }

        var applied = false;
        var errorChanged = false;
        var hadValue = false;
        T? previous = default;
        CancellationTokenSource? cancellation = null;
        TaskCompletionSource? settled = null;
        lock (_gate)
        {
            if (!_disposed && version == _version)
            {
                applied = true;
                cancellation = _cancellation;
                settled = _settled;
                _cancellation = null;
                _settled = null;
                _isLoading = false;

                if (error is null)
                {
                    hadValue = _hasValue;
                    previous = _value;
                    errorChanged = _error is not null;
                    _value = value;
                    _hasValue = true;
                    _error = null;
                }
                else
                {
                    errorChanged = true;
                    _error = error;
                }
            }
        }

        // Compared outside the lock: the comparer is user code.
        var valueChanged = applied && error is null && (!hadValue || !EqualityComparer<T>.Default.Equals(previous, value));

        cancellation?.Dispose();
        Publish(valueChanged, loadingChanged || applied, errorChanged);
        settled?.TrySetResult();
    }

    /// <summary>Records a failure of the source: there is nothing to fetch, so the fetch in flight is superseded.</summary>
    private void Fail(Exception error)
    {
        CancellationTokenSource? superseded;
        TaskCompletionSource? settled;
        bool loadingChanged;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _version++;
            superseded = _cancellation;
            settled = _settled;
            loadingChanged = _isLoading;
            _cancellation = null;
            _settled = null;
            _isLoading = false;
            _error = error;
        }

        Cancel(superseded);
        Publish(valueChanged: false, loadingChanged, errorChanged: true);
        settled?.TrySetResult();
    }

    /// <summary>Notifies the readers of the changed properties in one notification wave.</summary>
    private void Publish(bool valueChanged, bool loadingChanged, bool errorChanged)
    {
        if (!valueChanged && !loadingChanged && !errorChanged)
        {
            return;
        }

        ReactiveBatch.Enter();

        try
        {
            if (valueChanged)
            {
                ReactiveProperty.Notify(ref _valueNode);
            }

            if (loadingChanged)
            {
                ReactiveProperty.Notify(ref _loadingNode);
            }

            if (errorChanged)
            {
                ReactiveProperty.Notify(ref _errorNode);
            }
        }
        finally
        {
            ReactiveBatch.Exit();
        }
    }

    // Called without the lock: cancelling runs the superseded fetch's callbacks. Its outcome is ignored from now on,
    // and so are exceptions thrown by those callbacks.
    private static void Cancel(CancellationTokenSource? cancellation)
    {
        if (cancellation is null)
        {
            return;
        }

        try
        {
            cancellation.Cancel();
        }
        catch (AggregateException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    /// <summary>
    /// Re-evaluates the source when it changes and starts a fetch for each new result. Refreshes run one at a time on
    /// the thread that requested them; requests arriving meanwhile coalesce into one more refresh by that thread.
    /// </summary>
    private abstract class SourceWatcher(Resource<T> owner) : ReactiveObserver
    {
        private const int Idle = 0;
        private const int Running = 1;
        private const int RerunRequested = 2;

        // The creating thread runs the first refresh.
        private int _state = Running;
        private int _reloadRequested;

        protected Resource<T> Owner => owner;

        public void Reload()
        {
            Volatile.Write(ref _reloadRequested, 1);
            Request();
        }

        public void Run()
        {
            do
            {
                if (IsDisposed)
                {
                    Volatile.Write(ref _state, Idle);
                    return;
                }

                Refresh(reload: Interlocked.Exchange(ref _reloadRequested, 0) == 1);
            }
            while (Complete());
        }

        /// <summary>Stops watching and releases the source.</summary>
        public abstract void Stop();

        /// <summary>Reads the source and starts a fetch when its result changed, or always when reloading.</summary>
        protected abstract void Refresh(bool reload);

        protected override void OnInvalidated() => Request();

        private void Request()
        {
            while (true)
            {
                switch (Volatile.Read(ref _state))
                {
                    case Idle:
                        if (Interlocked.CompareExchange(ref _state, Running, Idle) == Idle)
                        {
                            Run();
                            return;
                        }

                        break;

                    case Running:
                        if (Interlocked.CompareExchange(ref _state, RerunRequested, Running) == Running)
                        {
                            return;
                        }

                        break;

                    default:
                        return;
                }
            }
        }

        /// <summary>Ends a refresh. Returns true when another one was requested meanwhile and should follow.</summary>
        private bool Complete()
        {
            if (Interlocked.CompareExchange(ref _state, Idle, Running) == Running)
            {
                return false;
            }

            // Requests arriving from now on ask for yet another refresh.
            Volatile.Write(ref _state, Running);

            return true;
        }
    }

    private sealed class SourceWatcher<TSource>(Resource<T> owner, Func<TSource> source, Func<TSource, CancellationToken, Task<T>> fetch)
        : SourceWatcher(owner)
    {
        private readonly Derived<TSource> _source = Reactive.Derived(source);
        private TSource _lastSource = default!;
        private bool _hasLastSource;

        public override void Stop()
        {
            Dispose();
            _source.Dispose();
        }

        protected override void Refresh(bool reload)
        {
            TSource value;
            try
            {
                // Subscribes this watcher to the derived source, which re-evaluates eagerly while observed and
                // notifies only when its result changes.
                using (BeginTracking())
                {
                    value = _source.Value;
                }
            }
            catch (Exception exception)
            {
                // Once the source recovers, even its previous result needs a new fetch.
                _hasLastSource = false;
                Owner.Fail(exception);

                return;
            }

            if (!reload && _hasLastSource && EqualityComparer<TSource>.Default.Equals(_lastSource, value))
            {
                return;
            }

            _lastSource = value;
            _hasLastSource = true;
            Owner.Start(fetch, value);
        }
    }
}

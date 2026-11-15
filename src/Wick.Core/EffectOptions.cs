namespace Wick.Core;

/// <summary>
/// Settings of a <see cref="ReactiveEffect"/>: error handling, where its runs happen and when, for example
/// <c>new EffectOptions { Debounce = TimeSpan.FromSeconds(2), FlushOnDispose = true }</c> for an autosave.
/// </summary>
public sealed record EffectOptions
{
    /// <summary>
    /// Receives the exceptions of runs. Without it, the first run of a synchronous effect propagates its exception to
    /// the caller, and later failures are reported through <see cref="Reactive.UnhandledEffectException"/>.
    /// </summary>
    public Action<Exception>? OnError { get; init; }

    /// <summary>
    /// Starts each re-run where it must happen, for example a Blazor component's <c>InvokeAsync</c> or a UI thread's
    /// dispatcher: it receives a function that performs the run, or for an asynchronous effect starts it, and returns
    /// once that part has ended. Without it, re-runs happen on the thread pool.
    /// </summary>
    public Func<Func<Task>, Task>? Dispatcher { get; init; }

    /// <summary>
    /// Delays each re-run until the state the effect read has stopped changing for this long. Zero, the default,
    /// re-runs right after a change. Cannot be combined with <see cref="Throttle"/>.
    /// </summary>
    public TimeSpan Debounce { get; init; }

    /// <summary>
    /// Starts runs at most once per this interval: a change after a quiet interval re-runs right away, and changes
    /// within the interval coalesce into one run at its end. Zero, the default, does not limit runs. Cannot be combined
    /// with <see cref="Debounce"/>.
    /// </summary>
    public TimeSpan Throttle { get; init; }

    /// <summary>
    /// When set, disposing the effect does not drop its pending work: a run delayed by <see cref="Debounce"/> or
    /// <see cref="Throttle"/>, or requested by a change during a run, still happens, once, without tracking; a run in
    /// progress is not cancelled. Meant for saving the last edit when its owner goes away.
    /// </summary>
    public bool FlushOnDispose { get; init; }

    /// <summary>The clock for <see cref="Debounce"/> and <see cref="Throttle"/>; <see cref="TimeProvider.System"/> by default.</summary>
    public TimeProvider? TimeProvider { get; init; }
}

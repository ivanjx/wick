namespace Wick.Core;

/// <summary>
/// A value that follows a reactive source once the source has stopped changing for a delay; created with
/// <see cref="Reactive.Debounced{T}"/>. Reading <see cref="Value"/> is tracked like a reactive property, so its
/// readers re-render or re-run only after the source has settled.
/// </summary>
/// <remarks>
/// The value starts as the source's current value. Dispose it when its owner goes away: it is referenced by the state
/// it reads and lives as long as that state otherwise.
/// </remarks>
public sealed class Debounced<T> : IDisposable
{
    private readonly ReactiveValue<T> _value = new(default!, "Debounced<T>");
    private readonly ReactiveEffect _follow;

    internal Debounced(Func<T> read, TimeSpan delay, TimeProvider? timeProvider)
    {
        _follow = SyncReactiveEffect.Start(
            () => _value.Value = read(),
            new EffectOptions { Debounce = delay, TimeProvider = timeProvider });
    }

    /// <summary>The source's value as of the last time it settled.</summary>
    public T Value => _value.Value;

    /// <summary>Stops following the source; a pending update is dropped.</summary>
    public void Dispose() => _follow.Dispose();
}

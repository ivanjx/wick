namespace Wick.Core;

/// <summary>
/// Active dependency collection for one <see cref="ReactiveObserver"/>. Dispose in a <c>finally</c>
/// (or <c>using</c>) so an exception during tracking cannot leave the thread's context corrupted.
/// </summary>
public readonly struct TrackingScope : IDisposable
{
    private readonly ReactiveObserver? _owner;
    private readonly ReactiveObserver? _previous;

    internal TrackingScope(ReactiveObserver owner, ReactiveObserver? previous)
    {
        _owner = owner;
        _previous = previous;
    }

    /// <summary>Ends dependency collection and restores the observer that was tracking before, if any.</summary>
    public void Dispose() => _owner?.EndTracking(_previous);
}

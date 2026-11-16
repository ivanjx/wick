namespace Wick.Core;

/// <summary>
/// The source of a <see cref="Reactive.Watch{T}(Func{T}, Action{T, T}, EffectOptions?, IEqualityComparer{T}?)"/>
/// and the value it last had. Read only from the effect's runs, which never overlap.
/// </summary>
internal sealed class WatchedSource<T>(Func<T> read, IEqualityComparer<T>? comparer)
{
    private readonly IEqualityComparer<T> _comparer = comparer ?? EqualityComparer<T>.Default;
    private bool _started;
    private T _value = default!;

    /// <summary>
    /// Reads the source, tracked by the running effect. True when its value differs from the previous read; the first
    /// read only records the value.
    /// </summary>
    public bool TryChange(out T value, out T previous)
    {
        value = read();
        previous = _value;

        if (!_started)
        {
            _started = true;
            _value = value;
            return false;
        }

        if (_comparer.Equals(previous, value))
        {
            return false;
        }

        _value = value;
        return true;
    }
}

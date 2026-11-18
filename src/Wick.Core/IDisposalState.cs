namespace Wick.Core;

/// <summary>
/// Implemented by the disposables a component owns (effects, debounced values), so it can drop the ones disposed early
/// instead of keeping them until it is disposed itself.
/// </summary>
internal interface IDisposalState
{
    bool IsDisposed { get; }
}

namespace Wick.Core;

/// <summary>Implemented by types that track dependencies through a private observer, so diagnostics can reach it.</summary>
internal interface IReactiveObserverOwner
{
    ReactiveObserver Observer { get; }
}

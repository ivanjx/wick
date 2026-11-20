using Wick.Core;

namespace Wick.Benchmarks.State;

/// <summary>The established pattern: a <see cref="Derived{T}"/> field created in the constructor.</summary>
[ReactiveState]
public sealed partial class DerivedCart
{
    private readonly Derived<decimal> _total;

    public DerivedCart() => _total = Reactive.Derived(() => Price * Quantity);

    public partial decimal Price { get; set; }

    public partial int Quantity { get; set; }

    public decimal Total => _total.Value;
}

[ReactiveState]
public sealed partial class MemoCart
{
    public partial decimal Price { get; set; }

    public partial int Quantity { get; set; }

    public decimal Total => Memo(() => Price * Quantity);

    public decimal TotalFast => Memo(static (MemoCart cart) => cart.Price * cart.Quantity);

    public decimal Uncached => Price * Quantity;
}

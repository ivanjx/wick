using Wick.Core;

namespace Wick.Sample.State;

[ReactiveState]
public sealed partial class CartLine
{
    public string Id { get; } = Guid.NewGuid().ToString("N");

    public required string Name { get; init; }

    public required decimal Price { get; init; }

    public partial int Quantity { get; set; } = 1;
}

/// <summary>
/// Computed properties three ways: plain getters, recomputed on every read; <c>Memo(() =&gt; ...)</c>, cached per
/// instance; and <c>Memo(static (CartState cart) =&gt; ...)</c>, cached and allocation-free to read. Memo values are
/// recomputed once when what they read changes, and their readers re-render only when the result differs.
/// </summary>
[ReactiveState]
public sealed partial class CartState
{
    public const decimal FreeShippingThreshold = 100m;

    private static readonly (string Name, decimal Price)[] Catalog =
    [
        ("Coffee beans", 14.50m),
        ("Notebook", 6.25m),
        ("Desk lamp", 39.90m),
        ("Headphones", 89.00m),
        ("Mug", 9.75m),
        ("Plant", 18.40m),
    ];

    // Plain fields, not reactive: they only count computations for the page to display.
    private int _shippingRuns;
    private int _uncachedShippingRuns;

    public ReactiveList<CartLine> Lines { get; } =
    [
        new() { Name = "Coffee beans", Price = 14.50m, Quantity = 2 },
        new() { Name = "Notebook", Price = 6.25m, Quantity = 3 },
    ];

    public partial int DiscountPercent { get; set; }

    // Cheap: a plain getter is fine. Readers still depend on every quantity it reads.
    public int ItemCount => Lines.Sum(line => line.Quantity);

    // Cached. The lambda captures `this`, so each read allocates a small delegate; the first one is kept.
    public decimal Subtotal => Memo(() => Lines.Sum(line => line.Price * line.Quantity));

    // Cached, and a static lambda is allocated once by the compiler, so reads don't allocate. Memos can read memos.
    public decimal Total => Memo(static (CartState cart) => Math.Round(cart.Subtotal * (100 - cart.DiscountPercent) / 100, 2));

    // Recomputed once whenever Total changes, but its readers re-render only when it flips.
    public bool FreeShipping => Memo(static (CartState cart) =>
    {
        Interlocked.Increment(ref cart._shippingRuns);
        return cart.Total >= FreeShippingThreshold;
    });

    // The same check as a plain getter: every reader recomputes it on every render, and re-renders whenever a
    // quantity or the discount changes, even when the answer stays the same.
    public bool UncachedFreeShipping
    {
        get
        {
            Interlocked.Increment(ref _uncachedShippingRuns);
            return Math.Round(Lines.Sum(line => line.Price * line.Quantity) * (100 - DiscountPercent) / 100, 2) >= FreeShippingThreshold;
        }
    }

    public int ShippingRuns => Volatile.Read(ref _shippingRuns);

    public int UncachedShippingRuns => Volatile.Read(ref _uncachedShippingRuns);

    public void AddRandom()
    {
        var (name, price) = Catalog[Random.Shared.Next(Catalog.Length)];
        var existing = Lines.FirstOrDefault(line => line.Name == name);

        if (existing is not null)
        {
            existing.Quantity++;
            return;
        }

        Lines.Add(new CartLine { Name = name, Price = price });
    }

    public void Decrement(CartLine line)
    {
        if (line.Quantity > 1)
        {
            line.Quantity--;
            return;
        }

        Lines.Remove(line);
    }
}

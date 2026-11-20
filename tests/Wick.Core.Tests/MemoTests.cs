using System.Runtime.CompilerServices;

namespace Wick.Core.Tests;

[ReactiveState]
internal sealed partial class CartState
{
    public int TotalComputations;
    public int FastComputations;
    public int RatioComputations;

    public partial decimal Price { get; set; }

    public partial int Quantity { get; set; }

    public partial bool UseLabel { get; set; }

    public partial string Label { get; set; } = "";

    public partial string Sku { get; set; } = "";

    public ReactiveList<int> Items { get; } = [];

    public decimal Total => Memo(() =>
    {
        Interlocked.Increment(ref TotalComputations);
        return Price * Quantity;
    });

    public decimal TotalFast => Memo(static (CartState state) =>
    {
        Interlocked.Increment(ref state.FastComputations);
        return state.Price * state.Quantity;
    });

    public bool IsExpensive => Memo(() => Price > 100);

    public string Caption => Memo(() => UseLabel ? Label : Sku);

    public decimal DoubleTotal => Memo(static (CartState state) => state.Total * 2);

    public int Ratio => Memo(() =>
    {
        Interlocked.Increment(ref RatioComputations);
        return 10 / Quantity;
    });

    public int ItemsSum => Memo(static (CartState state) => state.Items.Sum());

    public (decimal Price, int Quantity) Pair => (Memo(() => Price), Memo(() => Quantity));

    public int CycleA => Memo(() => CycleB + 1);

    public int CycleB => Memo(() => CycleA + 1);

    public decimal Uncached => Price * Quantity;
}

[ReactiveState]
internal sealed partial class MemoBox<T>
{
    public partial T Value { get; set; } = default!;

    public string Text => Memo(static (MemoBox<T> box) => $"[{box.Value}]");

    [ReactiveState]
    internal sealed partial class Nested
    {
        public partial int Count { get; set; }

        public int Twice => Memo(() => Count * 2);
    }
}

internal sealed class RuntimeCart : ReactiveObject
{
    public int Computations;

    public decimal Price { get => Get(ref field); set => Set(ref field, value); }

    public int Quantity { get => Get(ref field); set => Set(ref field, value); }

    public decimal Total => Memo(() =>
    {
        Interlocked.Increment(ref Computations);
        return Price * Quantity;
    });

    public decimal TotalFast => Memo(static (RuntimeCart cart) => cart.Price * cart.Quantity);

    public int WrongOwner => Memo(static (RuntimeState state) => state.Count);
}

public sealed class MemoTests
{
    [Fact]
    public void Both_forms_compute_once_and_later_reads_reuse_the_value()
    {
        var cart = new CartState { Price = 2, Quantity = 3 };

        for (var i = 0; i < 101; i++)
        {
            Assert.Equal(6, cart.Total);
            Assert.Equal(6, cart.TotalFast);
        }

        Assert.Equal(1, cart.TotalComputations);
        Assert.Equal(1, cart.FastComputations);
    }

    [Fact]
    public void Creation_is_lazy()
    {
        var cart = new CartState { Price = 2, Quantity = 3 };

        cart.Price = 4;

        Assert.Equal(0, cart.TotalComputations);
        Assert.Equal(12, cart.Total);
        Assert.Equal(1, cart.TotalComputations);
    }

    [Fact]
    public void An_unobserved_memo_recomputes_lazily_and_an_observed_one_eagerly()
    {
        var cart = new CartState { Price = 2, Quantity = 3 };
        _ = cart.Total;

        cart.Price = 3;
        cart.Price = 4;

        Assert.Equal(1, cart.TotalComputations);
        Assert.Equal(12, cart.Total);
        Assert.Equal(2, cart.TotalComputations);

        var observer = new CountingObserver();
        observer.Track(() => cart.Total);
        cart.Quantity = 1;

        Assert.Equal(3, cart.TotalComputations);
        Assert.Equal(1, observer.Invalidations);
        Assert.Equal(4, cart.Total);
        Assert.Equal(3, cart.TotalComputations);
    }

    [Fact]
    public void Instances_hold_independent_memos()
    {
        var first = new CartState { Price = 1, Quantity = 1 };
        var second = new CartState { Price = 5, Quantity = 2 };

        Assert.Equal(1, first.TotalFast);
        Assert.Equal(10, second.TotalFast);

        second.Price = 6;

        Assert.Equal(1, first.TotalFast);
        Assert.Equal(12, second.TotalFast);
        Assert.Equal(1, first.FastComputations);
    }

    [Fact]
    public void Readers_are_invalidated_only_when_the_result_changes()
    {
        var cart = new CartState { Price = 1 };
        var observer = new CountingObserver();

        observer.Track(() => cart.IsExpensive);
        cart.Price = 2;
        cart.Price = 50;

        Assert.Equal(0, observer.Invalidations);

        cart.Price = 200;

        Assert.Equal(1, observer.Invalidations);
        Assert.True(cart.IsExpensive);
    }

    [Fact]
    public void Conditional_dependencies_are_replaced()
    {
        var cart = new CartState { Sku = "A-1", Label = "Apple" };
        var observer = new CountingObserver();

        observer.Track(() => cart.Caption);
        cart.Label = "Pear";

        Assert.Equal(0, observer.Invalidations);

        cart.UseLabel = true;

        Assert.Equal(1, observer.Invalidations);
        Assert.Equal("Pear", observer.Track(() => cart.Caption));

        cart.Sku = "B-2";

        Assert.Equal(1, observer.Invalidations);
    }

    [Fact]
    public void Memos_can_read_other_memos()
    {
        var cart = new CartState { Price = 2, Quantity = 3 };
        var observer = new CountingObserver();

        Assert.Equal(12, observer.Track(() => cart.DoubleTotal));

        cart.Quantity = 4;

        Assert.Equal(1, observer.Invalidations);
        Assert.Equal(16, cart.DoubleTotal);
    }

    [Fact]
    public void A_cycle_fails_clearly()
    {
        var cart = new CartState();

        var exception = Assert.Throws<InvalidOperationException>(() => cart.CycleA);

        Assert.Contains("cannot read itself", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Exceptions_are_cached_until_a_dependency_changes()
    {
        var cart = new CartState { Quantity = 0 };

        Assert.Throws<DivideByZeroException>(() => cart.Ratio);
        Assert.Throws<DivideByZeroException>(() => cart.Ratio);
        Assert.Equal(1, cart.RatioComputations);

        cart.Quantity = 2;

        Assert.Equal(5, cart.Ratio);
        Assert.Equal(2, cart.RatioComputations);
    }

    [Fact]
    public void Collection_changes_update_the_memo()
    {
        var cart = new CartState();
        var observer = new CountingObserver();

        Assert.Equal(0, observer.Track(() => cart.ItemsSum));

        cart.Items.Add(3);
        cart.Items.Add(4);

        Assert.Equal(2, observer.Invalidations);
        Assert.Equal(7, cart.ItemsSum);
    }

    [Fact]
    public void Several_memos_on_one_line_are_distinct()
    {
        var cart = new CartState { Price = 7, Quantity = 3 };

        Assert.Equal((7m, 3), cart.Pair);

        cart.Quantity = 4;

        Assert.Equal((7m, 4), cart.Pair);
    }

    [Fact]
    public void Writes_in_a_batch_invalidate_readers_once()
    {
        var cart = new CartState { Price = 1, Quantity = 1 };
        var observer = new CountingObserver();
        observer.Track(() => cart.Total);

        Reactive.Batch(() =>
        {
            cart.Price = 2;
            cart.Quantity = 5;

            Assert.Equal(0, observer.Invalidations);
        });

        Assert.Equal(1, observer.Invalidations);
        Assert.Equal(2, cart.TotalComputations);
        Assert.Equal(10, cart.Total);
    }

    [Fact]
    public async Task Writes_on_another_thread_invalidate_readers()
    {
        var cart = new CartState { Price = 1, Quantity = 1 };
        var observer = new CountingObserver();
        observer.Track(() => cart.TotalFast);

        await Task.Run(() => cart.Price = 3, TestContext.Current.CancellationToken);

        Assert.Equal(1, observer.Invalidations);
        Assert.Equal(3, cart.TotalFast);
    }

    [Fact]
    public void Generic_and_nested_states_memoize()
    {
        var box = new MemoBox<int> { Value = 4 };
        var nested = new MemoBox<string>.Nested { Count = 2 };

        Assert.Equal("[4]", box.Text);
        Assert.Equal(4, nested.Twice);

        box.Value = 5;
        nested.Count = 3;

        Assert.Equal("[5]", box.Text);
        Assert.Equal(6, nested.Twice);
    }

    [Fact]
    public void ReactiveObject_memos_track_and_cache()
    {
        var cart = new RuntimeCart { Price = 2, Quantity = 3 };
        var observer = new CountingObserver();

        Assert.Equal(6, observer.Track(() => cart.Total));
        Assert.Equal(6, cart.Total);
        Assert.Equal(6, cart.TotalFast);
        Assert.Equal(1, cart.Computations);

        cart.Quantity = 4;

        Assert.Equal(1, observer.Invalidations);
        Assert.Equal(8, cart.Total);
        Assert.Equal(8, cart.TotalFast);
    }

    [Fact]
    public void ReactiveObject_memo_rejects_a_parameter_of_another_type()
    {
        var cart = new RuntimeCart();

        var exception = Assert.Throws<InvalidOperationException>(() => cart.WrongOwner);

        Assert.Contains("RuntimeState", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_call_site_read_as_another_type_throws()
    {
        MemoCache? cache = null;
        var owner = new object();

        Assert.Equal(1, MemoCache.Get(ref cache, owner, () => 1, "Member", "File.cs", 1, "x"));

        var exception = Assert.Throws<InvalidOperationException>(() => MemoCache.Get(ref cache, owner, () => "text", "Member", "File.cs", 1, "x"));

        Assert.Contains("Int32", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Memos_are_named_after_the_type_and_member_in_diagnostics()
    {
        var cart = new CartState();
        var observer = new CountingObserver();

        observer.Track(() => cart.Total);

        Assert.Contains("CartState.Total", WickDiagnostics.GetDependencies(observer));
    }

    [Fact]
    public void Concurrent_first_reads_create_one_memo()
    {
        for (var round = 0; round < 20; round++)
        {
            var cart = new CartState { Price = 2, Quantity = 3 };
            using var barrier = new Barrier(8);

            var results = Enumerable.Range(0, 8)
                .Select(_ => Task.Factory.StartNew(
                    () =>
                    {
                        barrier.SignalAndWait();
                        return (cart.Total, cart.TotalFast);
                    },
                    TaskCreationOptions.LongRunning))
                .ToArray();

            Task.WaitAll(results, TestContext.Current.CancellationToken);

            Assert.All(results, task => Assert.Equal((6m, 6m), task.Result));
            Assert.Equal(1, cart.TotalComputations);
            Assert.Equal(1, cart.FastComputations);
        }
    }

    [Fact]
    public async Task Concurrent_reads_and_writes_neither_corrupt_nor_deadlock()
    {
        var cart = new CartState { Price = 1, Quantity = 1 };
        var observer = new CountingObserver();
        observer.Track(() => cart.DoubleTotal);

        var readers = Enumerable.Range(0, 4).Select(reader => Task.Run(() =>
        {
            for (var i = 0; i < 2_000; i++)
            {
                _ = cart.DoubleTotal + cart.Total + cart.TotalFast;
                _ = cart.IsExpensive;
            }
        }));

        var writers = Enumerable.Range(0, 2).Select(writer => Task.Run(() =>
        {
            for (var i = 0; i < 2_000; i++)
            {
                cart.Price = i;
            }
        }));

        await Task.WhenAll(readers.Concat(writers)).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        cart.Quantity = 2;

        Assert.Equal(cart.Price * 2, cart.Total);
        Assert.Equal(cart.Price * 4, cart.DoubleTotal);
    }

    [Fact]
    public void A_forgotten_state_and_its_memos_are_collected()
    {
        var shared = new AppState { Count = 1 };
        var observer = new CountingObserver();

        var state = CreateAndRead(shared, observer);
        CollectGarbage();

        Assert.False(state.IsAlive);
        GC.KeepAlive(shared);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndRead(AppState shared, CountingObserver observer)
    {
        var cart = new SharedReader(shared) { Price = 2 };

        // Observed, and subscribed to long-lived state through the memo.
        Assert.Equal(2, observer.Track(() => cart.Scaled));

        return new WeakReference(cart);
    }

    [Fact]
    public void Static_form_reads_do_not_allocate_after_warm_up()
    {
        var cart = new CartState { Price = 2, Quantity = 3 };
        var runtime = new RuntimeCart { Price = 2, Quantity = 3 };

        for (var i = 0; i < 1_000; i++)
        {
            _ = cart.TotalFast;
            _ = runtime.TotalFast;
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1_000; i++)
        {
            _ = cart.TotalFast;
            _ = runtime.TotalFast;
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    private static void CollectGarbage()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }
}

[ReactiveState]
internal sealed partial class SharedReader(AppState shared)
{
    public partial decimal Price { get; set; }

    public decimal Scaled => Memo(() => Price * shared.Count);
}

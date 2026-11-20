using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using Wick.Benchmarks.State;
using Wick.Core;

namespace Wick.Benchmarks;

/// <summary>
/// <c>Memo</c> properties against a constructor-initialized <see cref="Derived{T}"/> field (the baseline) and an
/// uncached getter: warm reads, tracked warm reads (what a render does), first reads of a new instance, and warm
/// reads spread over many instances.
/// </summary>
[MemoryDiagnoser]
[CategoriesColumn]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class MemoBenchmarks
{
    private const int InstanceCount = 1_000;

    private readonly DerivedCart _derived = new() { Price = 2, Quantity = 3 };
    private readonly MemoCart _memo = new() { Price = 2, Quantity = 3 };
    private readonly NoopObserver _observer = new();
    private DerivedCart[] _derivedCarts = [];
    private MemoCart[] _memoCarts = [];

    [GlobalSetup]
    public void Setup()
    {
        _derivedCarts = [.. Enumerable.Range(0, InstanceCount).Select(i => new DerivedCart { Price = i, Quantity = 2 })];
        _memoCarts = [.. Enumerable.Range(0, InstanceCount).Select(i => new MemoCart { Price = i, Quantity = 2 })];

        foreach (var cart in _memoCarts)
        {
            _ = cart.Total + cart.TotalFast;
        }

        foreach (var cart in _derivedCarts)
        {
            _ = cart.Total;
        }
    }

    [GlobalCleanup]
    public void Cleanup() => _observer.Dispose();

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("Warm")]
    public decimal DerivedField() => _derived.Total;

    [Benchmark]
    [BenchmarkCategory("Warm")]
    public decimal MemoLambda() => _memo.Total;

    [Benchmark]
    [BenchmarkCategory("Warm")]
    public decimal MemoStatic() => _memo.TotalFast;

    [Benchmark]
    [BenchmarkCategory("Warm")]
    public decimal Uncached() => _memo.Uncached;

    [Benchmark(Baseline = true)]
    [BenchmarkCategory("WarmTracked")]
    public decimal TrackedDerivedField()
    {
        using (_observer.BeginTracking())
        {
            return _derived.Total;
        }
    }

    [Benchmark]
    [BenchmarkCategory("WarmTracked")]
    public decimal TrackedMemoLambda()
    {
        using (_observer.BeginTracking())
        {
            return _memo.Total;
        }
    }

    [Benchmark]
    [BenchmarkCategory("WarmTracked")]
    public decimal TrackedMemoStatic()
    {
        using (_observer.BeginTracking())
        {
            return _memo.TotalFast;
        }
    }

    [Benchmark]
    [BenchmarkCategory("WarmTracked")]
    public decimal TrackedUncached()
    {
        using (_observer.BeginTracking())
        {
            return _memo.Uncached;
        }
    }

    /// <summary>Includes creating the state; the baseline creates its derived value in the constructor.</summary>
    [Benchmark(Baseline = true)]
    [BenchmarkCategory("First")]
    public decimal FirstDerivedField() => new DerivedCart { Price = 2, Quantity = 3 }.Total;

    [Benchmark]
    [BenchmarkCategory("First")]
    public decimal FirstMemoLambda() => new MemoCart { Price = 2, Quantity = 3 }.Total;

    [Benchmark]
    [BenchmarkCategory("First")]
    public decimal FirstMemoStatic() => new MemoCart { Price = 2, Quantity = 3 }.TotalFast;

    [Benchmark]
    [BenchmarkCategory("First")]
    public decimal FirstUncached() => new MemoCart { Price = 2, Quantity = 3 }.Uncached;

    [Benchmark(Baseline = true, OperationsPerInvoke = InstanceCount)]
    [BenchmarkCategory("ManyInstances")]
    public decimal ManyDerivedField()
    {
        decimal sum = 0;
        foreach (var cart in _derivedCarts)
        {
            sum += cart.Total;
        }

        return sum;
    }

    [Benchmark(OperationsPerInvoke = InstanceCount)]
    [BenchmarkCategory("ManyInstances")]
    public decimal ManyMemoLambda()
    {
        decimal sum = 0;
        foreach (var cart in _memoCarts)
        {
            sum += cart.Total;
        }

        return sum;
    }

    [Benchmark(OperationsPerInvoke = InstanceCount)]
    [BenchmarkCategory("ManyInstances")]
    public decimal ManyMemoStatic()
    {
        decimal sum = 0;
        foreach (var cart in _memoCarts)
        {
            sum += cart.TotalFast;
        }

        return sum;
    }

    [Benchmark(OperationsPerInvoke = InstanceCount)]
    [BenchmarkCategory("ManyInstances")]
    public decimal ManyUncached()
    {
        decimal sum = 0;
        foreach (var cart in _memoCarts)
        {
            sum += cart.Uncached;
        }

        return sum;
    }
}
